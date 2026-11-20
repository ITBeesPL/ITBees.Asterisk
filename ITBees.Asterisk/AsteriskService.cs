using ITBees.Asterisk.Ami;
using ITBees.Asterisk.Configuration;
using ITBees.Asterisk.Host;
using ITBees.Asterisk.Provisioning;
using ITBees.Asterisk.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ITBees.Asterisk;

/// <summary>Entry point of the library (register with <c>services.AddAsterisk()</c>).</summary>
public interface IAsteriskService
{
    /// <summary>Is Asterisk installed on this host, which version, is the service running.</summary>
    Task<AsteriskInstallation> GetInstallationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs Asterisk: the distribution's package (apt) or, where there is none, a build from the official
    /// sources (15-60 minutes). Returns what was installed.
    /// </summary>
    Task<string> InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Latest step of a running (or the last) source build; null when there was none.</summary>
    Task<string?> GetInstallProgressAsync(CancellationToken cancellationToken = default);

    /// <summary>What applying the package would change; writes nothing.</summary>
    Task<AsteriskProvisioningResult> PlanAsync(AsteriskConfigurationPackage package, CancellationToken cancellationToken = default);

    /// <summary>Applies the package (idempotent). See <see cref="AsteriskProvisioner"/>.</summary>
    Task<AsteriskProvisioningResult> ApplyAsync(AsteriskConfigurationPackage package, AsteriskProvisioningOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registered phones, calls in progress and trunk registrations, read over AMI.</summary>
    Task<AsteriskStatus> GetStatusAsync(ManagerSettings manager, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets up a call between two numbers: rings <paramref name="fromExtension"/> first and, once it is picked up,
    /// dials <paramref name="toNumber"/> from it.
    /// </summary>
    Task OriginateAsync(ManagerSettings manager, string fromExtension, string toNumber, int ringSeconds = 30, CancellationToken cancellationToken = default);

    /// <summary>Ends a call by its channel name (from <see cref="AsteriskStatus.Channels"/>).</summary>
    Task HangupAsync(ManagerSettings manager, string channel, CancellationToken cancellationToken = default);

    /// <summary>Restarts the Asterisk service (drops calls in progress).</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs an Asterisk CLI command over AMI, e.g. "pjsip show endpoints".</summary>
    Task<string> CommandAsync(ManagerSettings manager, string command, CancellationToken cancellationToken = default);

    /// <summary>Opens an AMI session for anything the library has no method for.</summary>
    Task<IAmiConnection> ConnectAsync(AmiConnectionOptions options, CancellationToken cancellationToken = default);
}

public sealed class AsteriskService : IAsteriskService
{
    private readonly IAsteriskHost _host;

    public AsteriskService(IAsteriskHost host)
    {
        _host = host;
    }

    /// <summary>Opens AMI (test seam).</summary>
    internal Func<AmiConnectionOptions, CancellationToken, Task<IAmiConnection>> AmiFactory { get; set; } =
        async (options, ct) => await AmiConnection.ConnectAsync(options, ct);

    /// <summary>AMI connection options for a package's manager account on this host.</summary>
    public static AmiConnectionOptions ManagerConnection(ManagerSettings manager)
    {
        // A wildcard bind is reached over loopback.
        var host = manager.BindAddress is "0.0.0.0" or "::" or "" ? "127.0.0.1" : manager.BindAddress;
        return new AmiConnectionOptions { Host = host, Port = manager.Port, Username = manager.Username, Password = manager.Password };
    }

    public Task<AsteriskInstallation> GetInstallationAsync(CancellationToken cancellationToken = default) =>
        _host.GetInstallationAsync(cancellationToken);

    public Task<string> InstallAsync(CancellationToken cancellationToken = default) => _host.InstallAsync(cancellationToken);

    public Task<string?> GetInstallProgressAsync(CancellationToken cancellationToken = default) => _host.GetInstallProgressAsync(cancellationToken);

    public Task<AsteriskProvisioningResult> PlanAsync(AsteriskConfigurationPackage package, CancellationToken cancellationToken = default) =>
        ApplyAsync(package, new AsteriskProvisioningOptions { DryRun = true }, cancellationToken);

    public Task<AsteriskProvisioningResult> ApplyAsync(AsteriskConfigurationPackage package, AsteriskProvisioningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var provisioner = new AsteriskProvisioner(_host) { AmiFactory = AmiFactory };
        return provisioner.ApplyAsync(package, options, cancellationToken);
    }

    public async Task<AsteriskStatus> GetStatusAsync(ManagerSettings manager, CancellationToken cancellationToken = default)
    {
        await using var ami = await AmiFactory(ManagerConnection(manager), cancellationToken);
        return await AsteriskStatusReader.ReadAsync(ami, cancellationToken);
    }

    public async Task OriginateAsync(ManagerSettings manager, string fromExtension, string toNumber, int ringSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (IsDialable(fromExtension) == false)
            throw new ArgumentException("Digits only", nameof(fromExtension));
        if (IsDialable(toNumber) == false)
            throw new ArgumentException("Digits, * and # only", nameof(toNumber));

        await using var ami = await AmiFactory(ManagerConnection(manager), cancellationToken);
        await ami.SendAsync("Originate", new[]
        {
            new KeyValuePair<string, string>("Channel", "PJSIP/" + fromExtension),
            new KeyValuePair<string, string>("Context", AsteriskConfigRenderer.InternalContext),
            new KeyValuePair<string, string>("Exten", toNumber),
            new KeyValuePair<string, string>("Priority", "1"),
            new KeyValuePair<string, string>("CallerID", $"\"Octopark\" <{toNumber}>"),
            new KeyValuePair<string, string>("Timeout", (Math.Clamp(ringSeconds, 5, 300) * 1000).ToString()),
            // The answer comes when the call is queued, not when someone picks up.
            new KeyValuePair<string, string>("Async", "true"),
        }, cancellationToken);
    }

    public async Task HangupAsync(ManagerSettings manager, string channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel name is required", nameof(channel));
        await using var ami = await AmiFactory(ManagerConnection(manager), cancellationToken);
        await ami.SendAsync("Hangup", new[] { new KeyValuePair<string, string>("Channel", channel.Trim()) }, cancellationToken);
    }

    public Task RestartAsync(CancellationToken cancellationToken = default) => _host.ServiceAsync("restart", cancellationToken);

    public async Task<string> CommandAsync(ManagerSettings manager, string command, CancellationToken cancellationToken = default)
    {
        await using var ami = await AmiFactory(ManagerConnection(manager), cancellationToken);
        var response = await ami.SendAsync("Command", new[] { new KeyValuePair<string, string>("Command", command) }, cancellationToken);
        return string.Join('\n', response.Response.GetAll("Output"));
    }

    public async Task<IAmiConnection> ConnectAsync(AmiConnectionOptions options, CancellationToken cancellationToken = default) =>
        await AmiFactory(options, cancellationToken);

    private static bool IsDialable(string? number) =>
        string.IsNullOrEmpty(number) == false && number.Length <= 30 && number.All(c => char.IsAsciiDigit(c) || c is '*' or '#' or '+');
}

public static class AsteriskServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAsteriskService"/> for the Asterisk on this machine (singletons; nothing is started).
    /// </summary>
    public static IServiceCollection AddAsterisk(this IServiceCollection services, Action<AsteriskHostOptions>? configure = null)
    {
        var options = new AsteriskHostOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ISystemCommandRunner, ProcessCommandRunner>();
        services.TryAddSingleton<IAsteriskHost>(sp => new AsteriskHost(sp.GetRequiredService<ISystemCommandRunner>(), sp.GetRequiredService<AsteriskHostOptions>()));
        services.TryAddSingleton<IAsteriskService, AsteriskService>();
        return services;
    }
}
