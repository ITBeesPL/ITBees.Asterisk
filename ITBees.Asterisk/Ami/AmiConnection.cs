using System.Net.Sockets;
using System.Text;

namespace ITBees.Asterisk.Ami;

/// <summary>One AMI packet: "Key: Value" lines. A key may repeat (the "Output" lines of a CLI command).</summary>
public sealed class AmiMessage
{
    private readonly List<KeyValuePair<string, string>> _fields = new();

    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    public void Add(string key, string value) => _fields.Add(new KeyValuePair<string, string>(key, value));

    /// <summary>First value of the key (case-insensitive) or null.</summary>
    public string? this[string key]
    {
        get
        {
            foreach (var field in _fields)
            {
                if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
                    return field.Value;
            }

            return null;
        }
    }

    public IEnumerable<string> GetAll(string key) =>
        _fields.Where(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value);

    public bool IsResponse => this["Response"] != null;

    public bool IsSuccess => string.Equals(this["Response"], "Success", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(this["Response"], "Follows", StringComparison.OrdinalIgnoreCase);

    public string? Event => this["Event"];
}

/// <summary>Answer to one action: the response packet and, for list actions, the events that belong to it.</summary>
public sealed class AmiResponse
{
    public AmiResponse(AmiMessage response, IReadOnlyList<AmiMessage> events)
    {
        Response = response;
        Events = events;
    }

    public AmiMessage Response { get; }

    public IReadOnlyList<AmiMessage> Events { get; }
}

public sealed class AmiConnectionOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5038;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Longest wait for the answer to one action.</summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public interface IAmiConnection : IAsyncDisposable
{
    /// <summary>Banner of the manager, e.g. "Asterisk Call Manager/7.0.3".</summary>
    string? Banner { get; }

    /// <summary>
    /// Sends an action and waits for its answer. For list actions the events up to "EventList: Complete" are collected.
    /// </summary>
    /// <exception cref="AsteriskActionException">Asterisk answered "Response: Error".</exception>
    Task<AmiResponse> SendAsync(string action, IEnumerable<KeyValuePair<string, string>>? fields = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal Asterisk Manager Interface client: one action at a time over a TCP session, unsolicited events switched
/// off at login. Enough for status reads and call control; it is not an event listener.
/// </summary>
public sealed class AmiConnection : IAmiConnection
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly AmiConnectionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _actionId;

    private AmiConnection(TcpClient client, AmiConnectionOptions options)
    {
        _client = client;
        _options = options;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public string? Banner { get; private set; }

    /// <summary>Connects and logs in.</summary>
    /// <exception cref="AsteriskConnectionException">Nothing listens there or the peer is not an Asterisk manager.</exception>
    /// <exception cref="AsteriskAuthenticationException">Wrong user name or password.</exception>
    public static async Task<AmiConnection> ConnectAsync(AmiConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectTimeout);
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            client.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new AsteriskConnectionException(
                $"Asterisk manager (AMI) does not answer at {options.Host}:{options.Port} - Asterisk is stopped or the configuration has not been applied yet.", e);
        }

        var connection = new AmiConnection(client, options);
        try
        {
            connection.Banner = await connection.ReadLineAsync(options.ConnectTimeout, cancellationToken);
            if (connection.Banner == null || connection.Banner.StartsWith("Asterisk Call Manager", StringComparison.OrdinalIgnoreCase) == false)
                throw new AsteriskConnectionException($"{options.Host}:{options.Port} is not an Asterisk manager (banner: '{connection.Banner}').");

            try
            {
                await connection.SendAsync("Login", new[]
                {
                    new KeyValuePair<string, string>("Username", options.Username),
                    new KeyValuePair<string, string>("Secret", options.Password),
                    new KeyValuePair<string, string>("Events", "off"),
                }, cancellationToken);
            }
            catch (AsteriskActionException e)
            {
                throw new AsteriskAuthenticationException($"Asterisk manager refused the login of '{options.Username}': {e.Message}");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<AmiResponse> SendAsync(string action, IEnumerable<KeyValuePair<string, string>>? fields = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var actionId = "itbees-" + Interlocked.Increment(ref _actionId);
            var packet = new StringBuilder();
            AppendField(packet, "Action", action);
            AppendField(packet, "ActionID", actionId);
            if (fields != null)
            {
                foreach (var field in fields)
                    AppendField(packet, field.Key, field.Value);
            }

            packet.Append("\r\n");
            var bytes = Encoding.UTF8.GetBytes(packet.ToString());
            try
            {
                await _stream.WriteAsync(bytes, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            catch (IOException e)
            {
                throw new AsteriskConnectionException("The connection to the Asterisk manager was lost.", e);
            }

            var deadline = DateTime.UtcNow + _options.ActionTimeout;
            AmiMessage? response = null;
            var events = new List<AmiMessage>();
            while (true)
            {
                var message = await ReadMessageAsync(deadline, cancellationToken);
                if (message["ActionID"] != actionId)
                    continue; // An event that slipped in before "Events: off" took effect.

                if (response == null)
                {
                    if (message.IsResponse == false)
                        continue;
                    response = message;
                    if (response.IsSuccess == false)
                        throw new AsteriskActionException(action, response["Message"] ?? "error");
                    if (IsListStart(response) == false)
                        return new AmiResponse(response, events);
                    continue;
                }

                if (string.Equals(message["EventList"], "Complete", StringComparison.OrdinalIgnoreCase))
                    return new AmiResponse(response, events);
                events.Add(message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsListStart(AmiMessage response) =>
        string.Equals(response["EventList"], "start", StringComparison.OrdinalIgnoreCase);

    private static void AppendField(StringBuilder packet, string key, string? value)
    {
        // A line break in a value would start a new header or a new action.
        var clean = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        packet.Append(key).Append(": ").Append(clean).Append("\r\n");
    }

    private async Task<AmiMessage> ReadMessageAsync(DateTime deadline, CancellationToken cancellationToken)
    {
        var message = new AmiMessage();
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new AsteriskConnectionException("Asterisk manager did not answer in time.");
            var line = await ReadLineAsync(remaining, cancellationToken);
            if (line == null)
                throw new AsteriskConnectionException("Asterisk manager closed the connection.");
            if (line.Length == 0)
            {
                if (message.Fields.Count == 0)
                    continue;
                return message;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                // Raw output line of an old-style "Response: Follows" command.
                message.Add("Output", line);
                continue;
            }

            message.Add(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }
    }

    private async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
        {
            throw new AsteriskConnectionException("Asterisk manager did not answer in time.");
        }
        catch (IOException e)
        {
            throw new AsteriskConnectionException("The connection to the Asterisk manager was lost.", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.Connected)
            {
                var bye = Encoding.UTF8.GetBytes("Action: Logoff\r\n\r\n");
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await _stream.WriteAsync(bye, cts.Token);
            }
        }
        catch
        {
            // Closing anyway.
        }

        _reader.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }
}
