using System.Net;
using System.Net.Sockets;
using System.Text;
using ITBees.Asterisk.Ami;
using ITBees.Asterisk.Configuration;
using ITBees.Asterisk.Host;

namespace ITBees.Asterisk.Tests;

internal static class TestData
{
    public static AsteriskConfigurationPackage Package()
    {
        var package = new AsteriskConfigurationPackage();
        package.Manager.Password = "ManagerSecret1";
        package.Extensions.Add(new AsteriskExtension { Number = "100", Name = "Kasa", Password = "Secret-100x", DeviceModel = AsteriskDeviceModel.YealinkT30p });
        package.Extensions.Add(new AsteriskExtension { Number = "101", Name = "Biuro", Password = "Secret-101x", DeviceModel = AsteriskDeviceModel.FanvilX3sp });
        package.Extensions.Add(new AsteriskExtension
        {
            Number = "200", Name = "Wjazd", Password = "Secret-200x", DeviceModel = AsteriskDeviceModel.FanvilPa2s, HotlineTarget = "600",
        });
        package.RingGroups.Add(new AsteriskRingGroup { Number = "600", Name = "Obsluga", Members = { "100", "101" } });
        return package;
    }
}

/// <summary>In-memory Asterisk host: files in a dictionary, service calls recorded.</summary>
internal sealed class FakeHost : IAsteriskHost
{
    public AsteriskHostOptions Options { get; } = new();

    public AsteriskInstallation Installation { get; set; } = new() { Installed = true, Version = "20.6.0", ServiceActive = true, ServiceState = "active" };

    public Dictionary<string, string> Files { get; } = new();

    public List<string> Calls { get; } = new();

    public bool InstallSucceeds { get; set; } = true;

    public Task<AsteriskInstallation> GetInstallationAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installation);

    public Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("install");
        if (InstallSucceeds == false)
            throw new AsteriskHostException("no package");
        Installation = new AsteriskInstallation { Installed = true, Version = "20.6.0", ServiceActive = false, ServiceState = "inactive" };
        return Task.FromResult("done");
    }

    public Task<string?> GetInstallProgressAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> ReadConfigAsync(string fileName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(fileName, out var content) ? content : null);

    public Task WriteConfigAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        Calls.Add("write " + fileName);
        Files[fileName] = content;
        return Task.CompletedTask;
    }

    public Task ServiceAsync(string action, CancellationToken cancellationToken = default)
    {
        Calls.Add("service " + action);
        if (action is "start" or "restart")
            Installation.ServiceActive = true;
        return Task.CompletedTask;
    }

    public Task<string> CliAsync(string command, CancellationToken cancellationToken = default)
    {
        Calls.Add("cli " + command);
        return Task.FromResult(string.Empty);
    }
}

/// <summary>AMI stand-in that answers from a function.</summary>
internal sealed class FakeAmi : IAmiConnection
{
    private readonly Func<string, IReadOnlyList<KeyValuePair<string, string>>, AmiResponse> _answer;

    public FakeAmi(Func<string, IReadOnlyList<KeyValuePair<string, string>>, AmiResponse> answer)
    {
        _answer = answer;
    }

    public string? Banner => "Asterisk Call Manager/7.0.3";

    public List<(string Action, IReadOnlyList<KeyValuePair<string, string>> Fields)> Sent { get; } = new();

    public Task<AmiResponse> SendAsync(string action, IEnumerable<KeyValuePair<string, string>>? fields = null, CancellationToken cancellationToken = default)
    {
        var list = fields?.ToList() ?? new List<KeyValuePair<string, string>>();
        Sent.Add((action, list));
        return Task.FromResult(_answer(action, list));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static AmiMessage Message(params (string Key, string Value)[] fields)
    {
        var message = new AmiMessage();
        foreach (var (key, value) in fields)
            message.Add(key, value);
        return message;
    }

    public static AmiResponse Ok(params AmiMessage[] events) => new(Message(("Response", "Success")), events);

    /// <summary>A PBX that loaded the given endpoints.</summary>
    public static FakeAmi WithEndpoints(params string[] names) => new((action, _) => action switch
    {
        "PJSIPShowEndpoints" => Ok(names.Select(n => Message(("Event", "EndpointList"), ("ObjectName", n), ("DeviceState", "Unavailable"), ("ActiveChannels", "0"))).ToArray()),
        "CoreSettings" => new AmiResponse(Message(("Response", "Success"), ("AsteriskVersion", "20.6.0")), Array.Empty<AmiMessage>()),
        "CoreStatus" => new AmiResponse(Message(("Response", "Success"), ("CoreStartupDate", "2026-11-19"), ("CoreStartupTime", "08:00:00"), ("CoreCurrentCalls", "1")), Array.Empty<AmiMessage>()),
        _ => Ok(),
    });
}

/// <summary>A scripted AMI server on a loopback TCP port.</summary>
internal sealed class FakeAmiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeAmiServer(string password = "ManagerSecret1")
    {
        Password = password;
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public string Password { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<string> Actions { get; } = new();

    private async Task AcceptLoop()
    {
        try
        {
            while (_cts.IsCancellationRequested == false)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        async Task Send(string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")));

        await Send("Asterisk Call Manager/7.0.3\n");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
            {
                var i = line.IndexOf(':');
                fields[line[..i].Trim()] = line[(i + 1)..].Trim();
                continue;
            }

            var action = fields.GetValueOrDefault("Action") ?? string.Empty;
            var id = fields.GetValueOrDefault("ActionID");
            lock (Actions)
                Actions.Add(action);
            switch (action)
            {
                case "Login":
                    await Send(fields.GetValueOrDefault("Secret") == Password
                        ? $"Response: Success\nActionID: {id}\nMessage: Authentication accepted\n\n"
                        : $"Response: Error\nActionID: {id}\nMessage: Authentication failed\n\n");
                    break;
                case "PJSIPShowEndpoints":
                    await Send($"Response: Success\nActionID: {id}\nEventList: start\nMessage: A listing of Endpoints follows\n\n" +
                               // An unrelated event in between must be skipped.
                               "Event: FullyBooted\nStatus: Fully Booted\n\n" +
                               $"Event: EndpointList\nActionID: {id}\nObjectName: 100\nDeviceState: Not in use\nActiveChannels: 0\n\n" +
                               $"Event: EndpointList\nActionID: {id}\nObjectName: 101\nDeviceState: Unavailable\nActiveChannels: 0\n\n" +
                               $"Event: EndpointListComplete\nActionID: {id}\nEventList: Complete\nListItems: 2\n\n");
                    break;
                case "Command":
                    await Send($"Response: Success\nActionID: {id}\nMessage: Command output follows\nOutput: line one\nOutput: line two\n\n");
                    break;
                case "Logoff":
                    return;
                default:
                    await Send($"Response: Error\nActionID: {id}\nMessage: Invalid/unknown command\n\n");
                    break;
            }

            fields.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
            // Listener stopped.
        }
    }
}

internal sealed class FakeRunner : ISystemCommandRunner
{
    public List<string> Commands { get; } = new();

    public List<string?> Inputs { get; } = new();

    public Func<string, SystemCommandResult> Answer { get; set; } = _ => new SystemCommandResult(0, string.Empty, string.Empty);

    public Task<SystemCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var command = (fileName + " " + string.Join(' ', arguments)).Trim();
        Commands.Add(command);
        Inputs.Add(standardInput);
        return Task.FromResult(Answer(command));
    }
}
