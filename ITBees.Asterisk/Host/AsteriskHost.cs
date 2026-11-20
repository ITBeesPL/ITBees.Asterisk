using System.Text.RegularExpressions;
using ITBees.Asterisk.Configuration;

namespace ITBees.Asterisk.Host;

public sealed class AsteriskHostOptions
{
    public string ConfigDirectory { get; set; } = "/etc/asterisk";

    public string AsteriskBinary { get; set; } = "/usr/sbin/asterisk";

    public string ServiceName { get; set; } = "asterisk";

    /// <summary>
    /// Prefix privileged commands with "sudo -n" (never asks for a password; fails when the account has no
    /// password-less sudo). Ignored when the process already runs as root.
    /// </summary>
    public bool UseSudo { get; set; } = true;

    /// <summary>System account that owns the configuration files.</summary>
    public string FileOwner { get; set; } = "asterisk";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Build Asterisk from the official sources when the system's repositories have no "asterisk" package
    /// (Debian 12, Raspberry Pi OS bookworm). Takes 15-60 minutes on a small board.
    /// </summary>
    public bool BuildFromSourceWhenNoPackage { get; set; } = true;

    public TimeSpan SourceBuildTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Output of the source build; its "[itbees]" lines are the progress.</summary>
    public string InstallLogPath { get; set; } = "/var/log/itbees-asterisk-install.log";
}

/// <summary>What is on the host right now.</summary>
public sealed class AsteriskInstallation
{
    public bool Installed { get; set; }

    /// <summary>E.g. "20.6.0".</summary>
    public string? Version { get; set; }

    public bool ServiceActive { get; set; }

    /// <summary>Text of "systemctl is-active": active, inactive, failed...</summary>
    public string? ServiceState { get; set; }
}

/// <summary>
/// The Asterisk on the machine this code runs on: package, service and configuration files. Linux with systemd and
/// apt (Debian / Ubuntu / Raspberry Pi OS).
/// </summary>
public interface IAsteriskHost
{
    AsteriskHostOptions Options { get; }

    Task<AsteriskInstallation> GetInstallationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the distribution's asterisk package or, where there is none, builds Asterisk from the official
    /// sources. Returns a short description of what was installed.
    /// </summary>
    Task<string> InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Latest step of a running (or the last) source build, e.g. "5/7 compiling with 4 job(s)"; null when there is none.</summary>
    Task<string?> GetInstallProgressAsync(CancellationToken cancellationToken = default);

    /// <summary>Content of a file in the configuration directory, or null when it does not exist.</summary>
    Task<string?> ReadConfigAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Writes a file in the configuration directory; a file not written by the library before is kept as "*.itbees-orig".</summary>
    Task WriteConfigAsync(string fileName, string content, CancellationToken cancellationToken = default);

    /// <summary>systemctl start / stop / restart / enable.</summary>
    Task ServiceAsync(string action, CancellationToken cancellationToken = default);

    /// <summary>Runs an Asterisk CLI command ("asterisk -rx") and returns its output.</summary>
    Task<string> CliAsync(string command, CancellationToken cancellationToken = default);
}

public sealed partial class AsteriskHost : IAsteriskHost
{
    private readonly ISystemCommandRunner _runner;

    public AsteriskHost(ISystemCommandRunner runner, AsteriskHostOptions? options = null)
    {
        _runner = runner;
        Options = options ?? new AsteriskHostOptions();
    }

    public AsteriskHostOptions Options { get; }

    [GeneratedRegex(@"Asterisk\s+([0-9][0-9A-Za-z.~+-]*)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex FileNameRegex();

    public async Task<AsteriskInstallation> GetInstallationAsync(CancellationToken cancellationToken = default)
    {
        var installation = new AsteriskInstallation();
        var version = await _runner.RunAsync(Options.AsteriskBinary, new[] { "-V" }, null, Options.CommandTimeout, cancellationToken);
        if (version.Success == false)
            return installation;

        installation.Installed = true;
        var match = VersionRegex().Match(version.Output);
        installation.Version = match.Success ? match.Groups[1].Value : version.Output.Trim();

        var active = await _runner.RunAsync("systemctl", new[] { "is-active", Options.ServiceName }, null, Options.CommandTimeout, cancellationToken);
        installation.ServiceState = active.Started ? active.Output.Trim() : null;
        installation.ServiceActive = installation.ServiceState == "active";
        return installation;
    }

    public async Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        // A stale package index is the usual reason "apt-get install" cannot find anything; a failed update is not fatal.
        await Privileged(new[] { "apt-get", "update" }, null, TimeSpan.FromMinutes(5), cancellationToken);
        var install = await Privileged(
            new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--no-install-recommends", "asterisk" },
            null, Options.InstallTimeout, cancellationToken);
        if (install.Success == false)
        {
            // apt prints the reason on stdout or stderr depending on its version.
            var text = (install.Output + "\n" + install.Error).Trim();
            var noPackage = text.Contains("Unable to locate package", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("no installation candidate", StringComparison.OrdinalIgnoreCase);
            if (noPackage && Options.BuildFromSourceWhenNoPackage)
                return await InstallFromSourceAsync(cancellationToken);
            if (noPackage)
            {
                throw new AsteriskHostException(
                    "This system's package repositories have no 'asterisk' package (e.g. Debian 12 dropped it) - install Asterisk by hand, then apply the configuration.");
            }

            throw new AsteriskHostException("Installing the asterisk package failed: " + install.Describe());
        }

        var installed = await GetInstallationAsync(cancellationToken);
        return $"Asterisk {installed.Version} installed from the system package.";
    }

    /// <summary>The build script shipped with the library (Host/install-from-source.sh).</summary>
    internal static string ReadSourceInstallScript()
    {
        using var stream = typeof(AsteriskHost).Assembly.GetManifestResourceStream("ITBees.Asterisk.install-from-source.sh")
                           ?? throw new AsteriskHostException("The Asterisk build script is missing from the library.");
        using var reader = new StreamReader(stream);
        // A Windows checkout may have given the file CRLF line ends, which bash does not take.
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private async Task<string> InstallFromSourceAsync(CancellationToken cancellationToken)
    {
        // The script arrives on stdin and its whole output goes to the log, where the progress is read from.
        var build = await Privileged(new[] { "bash", "-c", $"bash -s > '{Options.InstallLogPath}' 2>&1" },
            ReadSourceInstallScript(), Options.SourceBuildTimeout, cancellationToken);
        if (build.Success == false)
        {
            var progress = await GetInstallProgressAsync(cancellationToken);
            var tail = await ReadLogTailAsync(4, cancellationToken);
            throw new AsteriskHostException(
                $"Building Asterisk from the sources failed{(progress == null ? string.Empty : $" at step {progress}")}: " +
                $"{(tail.Length > 0 ? tail : build.Describe())} (full output: {Options.InstallLogPath})");
        }

        var installed = await GetInstallationAsync(cancellationToken);
        if (installed.Installed == false)
            throw new AsteriskHostException($"The build finished but the asterisk program is missing (see {Options.InstallLogPath}).");
        return $"Asterisk {installed.Version} built and installed from the official sources.";
    }

    public async Task<string?> GetInstallProgressAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(Options.InstallLogPath) == false)
                return null;
            // Shared read: the build is still writing to the file.
            await using var stream = new FileStream(Options.InstallLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? last = null;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("[itbees] ", StringComparison.Ordinal))
                    last = line;
            }

            // "[itbees] 12:01:44 5/7 compiling..." -> "5/7 compiling... (since 12:01:44)"
            if (last == null)
                return null;
            var parts = last.Split(' ', 3);
            return parts.Length == 3 ? $"{parts[2]} (since {parts[1]})" : last;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string> ReadLogTailAsync(int lines, CancellationToken cancellationToken)
    {
        try
        {
            var all = await File.ReadAllLinesAsync(Options.InstallLogPath, cancellationToken);
            return string.Join(" | ", all.Where(x => string.IsNullOrWhiteSpace(x) == false).TakeLast(lines).Select(x => x.Trim()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public async Task<string?> ReadConfigAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = ConfigPath(fileName);
        try
        {
            if (File.Exists(path) == false)
            {
                // The directory may be unreadable for this account: ask with privileges before saying "missing".
                var probe = await Privileged(new[] { "cat", path }, null, Options.CommandTimeout, cancellationToken);
                return probe.Success ? Normalize(probe.Output) : null;
            }

            return Normalize(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            var cat = await Privileged(new[] { "cat", path }, null, Options.CommandTimeout, cancellationToken);
            if (cat.Success == false)
                throw new AsteriskHostException($"Cannot read {path}: {cat.Describe()}", e);
            return Normalize(cat.Output);
        }
    }

    public async Task WriteConfigAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var path = ConfigPath(fileName);
        var existing = await ReadConfigAsync(fileName, cancellationToken);
        if (existing != null && existing.StartsWith(AsteriskConfigRenderer.ManagedMarker, StringComparison.Ordinal) == false)
        {
            // Keep the distribution's file once; "-n" never overwrites an earlier backup.
            await Privileged(new[] { "cp", "-n", "-p", path, path + ".itbees-orig" }, null, Options.CommandTimeout, cancellationToken);
        }

        var write = await Privileged(new[] { "tee", path }, Normalize(content), Options.CommandTimeout, cancellationToken);
        if (write.Success == false)
            throw new AsteriskHostException($"Cannot write {path}: {write.Describe()}");

        // The files hold passwords: owner and group only. A missing "asterisk" account is not fatal here.
        await Privileged(new[] { "chown", $"{Options.FileOwner}:{Options.FileOwner}", path }, null, Options.CommandTimeout, cancellationToken);
        await Privileged(new[] { "chmod", "640", path }, null, Options.CommandTimeout, cancellationToken);
    }

    public async Task ServiceAsync(string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("start" or "stop" or "restart" or "enable"))
            throw new ArgumentException("start, stop, restart or enable", nameof(action));
        var result = await Privileged(new[] { "systemctl", action, Options.ServiceName }, null, TimeSpan.FromSeconds(90), cancellationToken);
        if (result.Success == false)
            throw new AsteriskHostException($"systemctl {action} {Options.ServiceName} failed: {result.Describe()}");
    }

    public async Task<string> CliAsync(string command, CancellationToken cancellationToken = default)
    {
        var result = await Privileged(new[] { Options.AsteriskBinary, "-rx", command }, null, Options.CommandTimeout, cancellationToken);
        if (result.Success == false)
            throw new AsteriskHostException($"asterisk -rx \"{command}\" failed: {result.Describe()}");
        return result.Output.Trim();
    }

    private string ConfigPath(string fileName)
    {
        if (FileNameRegex().IsMatch(fileName) == false || fileName.Contains(".."))
            throw new ArgumentException("A plain file name is expected", nameof(fileName));
        return Options.ConfigDirectory.TrimEnd('/') + "/" + fileName;
    }

    private Task<SystemCommandResult> Privileged(string[] command, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sudo = Options.UseSudo && string.Equals(Environment.UserName, "root", StringComparison.Ordinal) == false;
        return sudo
            ? _runner.RunAsync("sudo", new[] { "-n" }.Concat(command).ToArray(), input, timeout, cancellationToken)
            : _runner.RunAsync(command[0], command.Skip(1).ToArray(), input, timeout, cancellationToken);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
