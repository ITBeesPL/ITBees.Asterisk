using System.Text.RegularExpressions;
using ITBees.Asterisk.Ami;
using ITBees.Asterisk.Configuration;
using ITBees.Asterisk.Host;

namespace ITBees.Asterisk.Provisioning;

public enum AsteriskStepStatus
{
    Ok = 0,
    Changed = 1,
    Skipped = 2,
    Failed = 3,
}

public sealed class AsteriskProvisioningStep
{
    public string Name { get; set; } = string.Empty;

    public AsteriskStepStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class AsteriskProvisioningResult
{
    public bool Success { get; set; }

    /// <summary>True for a plan: nothing was written.</summary>
    public bool DryRun { get; set; }

    public List<AsteriskProvisioningStep> Steps { get; } = new();

    /// <summary>What an apply changed (or a plan would change), one line each. Never holds passwords.</summary>
    public List<string> Changes { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool Restarted { get; set; }

    public bool Reloaded { get; set; }

    public string? Version { get; set; }

    /// <summary>Extensions Asterisk loaded after the apply.</summary>
    public int? LoadedEndpoints { get; set; }

    public string Summary { get; set; } = string.Empty;
}

public sealed class AsteriskProvisioningOptions
{
    /// <summary>Only report what would change.</summary>
    public bool DryRun { get; set; }

    /// <summary>Install the asterisk package when it is missing instead of failing.</summary>
    public bool InstallIfMissing { get; set; }

    /// <summary>How long to wait for AMI after a restart.</summary>
    public TimeSpan StartupWait { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Brings the Asterisk on this host to the state of a package: renders the files, writes the ones that differ,
/// switches off the legacy chan_sip driver (it would take the SIP port), then reloads or restarts and checks the
/// result over AMI. Idempotent.
/// </summary>
public sealed partial class AsteriskProvisioner
{
    public const string ModulesFile = "modules.conf";
    private const string NoLoadChanSip = "noload => chan_sip.so";

    private readonly IAsteriskHost _host;

    public AsteriskProvisioner(IAsteriskHost host)
    {
        _host = host;
    }

    /// <summary>Opens AMI with the package's manager account (test seam).</summary>
    internal Func<AmiConnectionOptions, CancellationToken, Task<IAmiConnection>> AmiFactory { get; set; } =
        async (options, ct) => await AmiConnection.ConnectAsync(options, ct);

    internal TimeSpan StartupPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"^\s*noload\s*=>?\s*chan_sip\.so", RegexOptions.Multiline)]
    private static partial Regex NoLoadChanSipRegex();

    public async Task<AsteriskProvisioningResult> ApplyAsync(AsteriskConfigurationPackage package, AsteriskProvisioningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AsteriskProvisioningOptions();
        var result = new AsteriskProvisioningResult { DryRun = options.DryRun };
        try
        {
            await Run(package, options, result, cancellationToken);
        }
        catch (AsteriskException e)
        {
            Step(result, "Error", AsteriskStepStatus.Failed, e.Message);
        }

        result.Success = result.Steps.All(x => x.Status != AsteriskStepStatus.Failed);
        var failed = result.Steps.FirstOrDefault(x => x.Status == AsteriskStepStatus.Failed);
        result.Summary = failed != null
            ? $"{failed.Name}: {failed.Message}"
            : options.DryRun
                ? result.Changes.Count == 0 ? "Nothing to change." : $"{result.Changes.Count} change(s) to apply."
                : result.Changes.Count == 0 ? "Already up to date." : $"Applied {result.Changes.Count} change(s).";
        return result;
    }

    private async Task Run(AsteriskConfigurationPackage package, AsteriskProvisioningOptions options, AsteriskProvisioningResult result,
        CancellationToken cancellationToken)
    {
        var validation = package.Validate();
        result.Warnings.AddRange(validation.Warnings);
        if (validation.IsValid == false)
        {
            Step(result, "Configuration", AsteriskStepStatus.Failed, string.Join(" | ", validation.Errors));
            return;
        }

        Step(result, "Configuration", AsteriskStepStatus.Ok,
            $"{package.Extensions.Count(x => x.Enabled)} extension(s), {package.RingGroups.Count} ring group(s), {package.Trunks.Count(x => x.Enabled)} trunk(s)");

        var installation = await _host.GetInstallationAsync(cancellationToken);
        if (installation.Installed == false)
        {
            if (options.DryRun)
            {
                result.Changes.Add("install the asterisk package");
                Step(result, "Asterisk", AsteriskStepStatus.Changed, "not installed - it would be installed first");
            }
            else if (options.InstallIfMissing)
            {
                await _host.InstallAsync(cancellationToken);
                installation = await _host.GetInstallationAsync(cancellationToken);
                if (installation.Installed == false)
                {
                    Step(result, "Asterisk", AsteriskStepStatus.Failed, "the package was installed but the asterisk program is still missing");
                    return;
                }

                result.Changes.Add($"installed Asterisk {installation.Version}");
                Step(result, "Asterisk", AsteriskStepStatus.Changed, $"installed version {installation.Version}");
            }
            else
            {
                Step(result, "Asterisk", AsteriskStepStatus.Failed, "Asterisk is not installed on this device - install it first.");
                return;
            }
        }
        else
        {
            Step(result, "Asterisk", AsteriskStepStatus.Ok, $"version {installation.Version}, service {installation.ServiceState ?? "unknown"}");
        }

        result.Version = installation.Version;

        var files = AsteriskConfigRenderer.Render(package);
        var changedFiles = new List<string>();
        var transportChanged = false;
        foreach (var (name, content) in files)
        {
            var current = installation.Installed ? await _host.ReadConfigAsync(name, cancellationToken) : null;
            if (current == content)
                continue;

            changedFiles.Add(name);
            result.Changes.Add(current == null ? $"create {name}" : $"update {name}{DescribeDifference(name, current, content)}");
            if (name == AsteriskConfigRenderer.PjsipFile &&
                AsteriskConfigRenderer.TransportFingerprint(current) != AsteriskConfigRenderer.TransportFingerprint(content))
            {
                transportChanged = true;
            }

            if (options.DryRun == false)
                await _host.WriteConfigAsync(name, content, cancellationToken);
        }

        var modules = installation.Installed ? await _host.ReadConfigAsync(ModulesFile, cancellationToken) : null;
        var modulesChanged = modules == null || NoLoadChanSipRegex().IsMatch(modules) == false;
        if (modulesChanged)
        {
            changedFiles.Add(ModulesFile);
            result.Changes.Add("switch off the legacy chan_sip driver (modules.conf)");
            if (options.DryRun == false)
            {
                var content = modules == null
                    ? $"[modules]\nautoload=yes\n{NoLoadChanSip}\n"
                    : modules.TrimEnd('\n') + $"\n; added by ITBees.Asterisk: chan_sip would take the SIP port of PJSIP\n{NoLoadChanSip}\n";
                await _host.WriteConfigAsync(ModulesFile, content, cancellationToken);
            }
        }

        Step(result, "Files", changedFiles.Count > 0 ? AsteriskStepStatus.Changed : AsteriskStepStatus.Ok,
            changedFiles.Count > 0 ? string.Join(", ", changedFiles) : "no differences");

        if (options.DryRun)
        {
            if (installation.Installed && installation.ServiceActive == false)
                result.Changes.Add("start the asterisk service");
            return;
        }

        // Transports, RTP ports, the HTTP server, the manager and the module list are only read at start.
        var needsRestart = installation.ServiceActive == false || transportChanged || modulesChanged ||
                           changedFiles.Any(x => x is AsteriskConfigRenderer.RtpFile or AsteriskConfigRenderer.HttpFile or AsteriskConfigRenderer.ManagerFile);
        if (needsRestart)
        {
            await _host.ServiceAsync("enable", cancellationToken);
            await _host.ServiceAsync("restart", cancellationToken);
            result.Restarted = true;
            if (installation.ServiceActive == false)
                result.Changes.Add("started the asterisk service");
            Step(result, "Service", AsteriskStepStatus.Changed, "restarted");
        }
        else if (changedFiles.Count > 0)
        {
            await _host.CliAsync("core reload", cancellationToken);
            result.Reloaded = true;
            Step(result, "Service", AsteriskStepStatus.Changed, "configuration reloaded (running calls are kept)");
        }
        else
        {
            Step(result, "Service", AsteriskStepStatus.Ok, "running, nothing to reload");
        }

        await Verify(package, options, result, cancellationToken);
    }

    private async Task Verify(AsteriskConfigurationPackage package, AsteriskProvisioningOptions options, AsteriskProvisioningResult result,
        CancellationToken cancellationToken)
    {
        var amiOptions = AsteriskService.ManagerConnection(package.Manager);
        var deadline = DateTime.UtcNow + (result.Restarted ? options.StartupWait : TimeSpan.FromSeconds(6));
        AsteriskException? last = null;
        while (true)
        {
            try
            {
                await using var ami = await AmiFactory(amiOptions, cancellationToken);
                var expected = package.Extensions.Count(x => x.Enabled) + package.Trunks.Count(x => x.Enabled);
                var loaded = 0;
                try
                {
                    loaded = (await ami.SendAsync("PJSIPShowEndpoints", null, cancellationToken)).Events.Count(x => x.Event == "EndpointList");
                }
                catch (AsteriskActionException)
                {
                    // "No endpoints found".
                }

                result.LoadedEndpoints = loaded;
                // A just restarted Asterisk answers AMI before PJSIP has finished loading.
                if (loaded < expected && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(StartupPollInterval, cancellationToken);
                    continue;
                }

                if (loaded < expected)
                {
                    Step(result, "Check", AsteriskStepStatus.Failed,
                        $"Asterisk loaded {loaded} of {expected} SIP accounts - see /var/log/asterisk/messages (a port in use or a missing codec module are the usual reasons).");
                }
                else
                {
                    Step(result, "Check", AsteriskStepStatus.Ok, $"Asterisk answers, {loaded} SIP account(s) loaded");
                }

                return;
            }
            catch (AsteriskAuthenticationException e)
            {
                Step(result, "Check", AsteriskStepStatus.Failed, e.Message);
                return;
            }
            catch (AsteriskConnectionException e)
            {
                last = e;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Step(result, "Check", AsteriskStepStatus.Failed, last.Message);
                return;
            }

            await Task.Delay(StartupPollInterval, cancellationToken);
        }
    }

    /// <summary>" (+2 / -1 lines)" - sizes only: the files hold passwords, their lines never go into a result.</summary>
    private static string DescribeDifference(string name, string current, string content)
    {
        if (current.StartsWith(AsteriskConfigRenderer.ManagedMarker, StringComparison.Ordinal) == false)
            return $" (replaces the existing file, kept as {name}.itbees-orig)";
        var before = current.Split('\n').ToHashSet();
        var after = content.Split('\n').ToHashSet();
        var added = after.Count(x => before.Contains(x) == false);
        var removed = before.Count(x => after.Contains(x) == false);
        return $" (+{added} / -{removed} lines)";
    }

    private static void Step(AsteriskProvisioningResult result, string name, AsteriskStepStatus status, string message) =>
        result.Steps.Add(new AsteriskProvisioningStep { Name = name, Status = status, Message = message });
}
