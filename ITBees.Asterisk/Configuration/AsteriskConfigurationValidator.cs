using System.Net;
using System.Text.RegularExpressions;

namespace ITBees.Asterisk.Configuration;

public sealed class AsteriskValidationResult
{
    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks a package before it is rendered. Besides plain mistakes it refuses every value that could break out of
/// its line in an Asterisk configuration file (new lines, ';', brackets), because the values come from a web form.
/// </summary>
public static partial class AsteriskConfigurationValidator
{
    public const int MinPasswordLength = 8;

    [GeneratedRegex("^[0-9]{2,10}$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,30}$")]
    private static partial Regex TokenRegex();

    // Secrets end up as "password=<value>": no white space, no comment / section / quote characters.
    [GeneratedRegex("^[A-Za-z0-9!#$%&*+,./:=?@^_~-]+$")]
    private static partial Regex SecretRegex();

    [GeneratedRegex("^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^[0-9XZN.!\[\]*#+-]{1,40}$")]
    private static partial Regex PatternRegex();

    [GeneratedRegex("^[*#][0-9*#]{1,5}$")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex("^[a-z0-9]{2,12}$")]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"^[\p{L}\p{N} ._()/+-]*$")]
    private static partial Regex DisplayNameRegex();

    public static AsteriskValidationResult Validate(AsteriskConfigurationPackage package)
    {
        var result = new AsteriskValidationResult();
        var errors = result.Errors;

        ValidateSip(package, result);

        if (IsPort(package.Rtp.PortStart) == false || IsPort(package.Rtp.PortEnd) == false || package.Rtp.PortEnd - package.Rtp.PortStart < 9)
            errors.Add("RTP port range must hold at least 10 ports between 1024 and 65535.");

        if (package.WebRtc.Enabled)
        {
            if (IsPort(package.WebRtc.HttpPort) == false)
                errors.Add("WebRTC HTTP port must be between 1024 and 65535.");
            if (IPAddress.TryParse(package.WebRtc.BindAddress, out _) == false)
                errors.Add("WebRTC bind address must be an IP address.");
            if (package.WebRtc.TlsEnabled && (IsPort(package.WebRtc.TlsPort) == false || package.WebRtc.TlsPort == package.WebRtc.HttpPort))
                errors.Add("WebRTC TLS port must be between 1024 and 65535 and differ from the HTTP port.");
        }

        if (IPAddress.TryParse(package.Manager.BindAddress, out var managerBind) == false)
            errors.Add("Manager (AMI) bind address must be an IP address.");
        else if (IPAddress.IsLoopback(managerBind) == false)
            result.Warnings.Add("Manager (AMI) listens beyond the loopback address - anyone in the network can try its password.");
        if (IsPort(package.Manager.Port) == false)
            errors.Add("Manager (AMI) port must be between 1024 and 65535.");
        if (TokenRegex().IsMatch(package.Manager.Username) == false)
            errors.Add("Manager (AMI) user name: letters, digits, '-' and '_' only.");
        CheckSecret(package.Manager.Password, "Manager (AMI) password", errors);

        if (package.Dialplan.RingSeconds is < 5 or > 300)
            errors.Add("Ring time must be between 5 and 300 seconds.");
        var prefix = package.Dialplan.IntercomPrefix;
        if (string.IsNullOrEmpty(prefix) == false && PrefixRegex().IsMatch(prefix) == false)
            errors.Add("Intercom prefix must start with * or # followed by up to 5 digits, e.g. *80.");

        var numbers = new Dictionary<string, string>();
        foreach (var extension in package.Extensions)
        {
            var label = $"Extension {extension.Number}";
            if (NumberRegex().IsMatch(extension.Number) == false)
            {
                errors.Add($"Extension number '{extension.Number}' must be 2-10 digits.");
                continue;
            }

            if (numbers.TryAdd(extension.Number, label) == false)
                errors.Add($"Number {extension.Number} is used twice.");
            CheckDisplayName(extension.Name, label, errors);
            CheckSecret(extension.Password, $"{label}: password", errors);
            if (extension.Password == extension.Number)
                errors.Add($"{label}: the password must differ from the number.");
            if (extension.MaxContacts is < 1 or > 10)
                errors.Add($"{label}: registered devices must be between 1 and 10.");
            CheckCodecs(extension.Codecs, label, errors);
            if (extension.DeviceModel == AsteriskDeviceModel.WebRtcBrowser && package.WebRtc.Enabled == false && extension.Enabled)
                errors.Add($"{label}: a browser phone needs WebRTC switched on.");
            if (string.IsNullOrEmpty(extension.Mac) == false && Regex.IsMatch(extension.Mac, "^[0-9A-Fa-f:.-]{12,17}$") == false)
                errors.Add($"{label}: MAC address looks wrong.");
        }

        foreach (var group in package.RingGroups)
        {
            var label = $"Ring group {group.Number}";
            if (NumberRegex().IsMatch(group.Number) == false)
            {
                errors.Add($"Ring group number '{group.Number}' must be 2-10 digits.");
                continue;
            }

            if (numbers.TryAdd(group.Number, label) == false)
                errors.Add($"Number {group.Number} is used twice.");
            CheckDisplayName(group.Name, label, errors);
            if (group.Members.Count == 0)
                errors.Add($"{label}: add at least one member.");
            if (group.Members.Count > 20)
                errors.Add($"{label}: at most 20 members.");
            if (group.RingSeconds != 0 && group.RingSeconds is < 5 or > 300)
                errors.Add($"{label}: ring time must be between 5 and 300 seconds.");
            foreach (var member in group.Members)
            {
                if (package.Extensions.Any(x => x.Number == member) == false)
                    errors.Add($"{label}: member {member} is not an extension.");
            }
        }

        // Targets are checked once every number is known.
        foreach (var extension in package.Extensions)
        {
            CheckTarget(extension.HotlineTarget, extension.Number, $"Extension {extension.Number}: hotline", numbers, errors);
            CheckTarget(extension.NoAnswerTarget, extension.Number, $"Extension {extension.Number}: no-answer target", numbers, errors);
        }

        foreach (var group in package.RingGroups)
            CheckTarget(group.NoAnswerTarget, group.Number, $"Ring group {group.Number}: no-answer target", numbers, errors);

        CheckNoAnswerLoops(package, errors);

        if (package.Extensions.Count > 0 && package.Extensions.All(x => x.Enabled == false))
            result.Warnings.Add("Every extension is disabled.");

        var trunkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trunk in package.Trunks)
        {
            var label = $"Trunk {trunk.Name}";
            if (TokenRegex().IsMatch(trunk.Name) == false)
            {
                errors.Add($"Trunk name '{trunk.Name}': letters, digits, '-' and '_' only (max 30).");
                continue;
            }

            if (trunkNames.Add(trunk.Name) == false)
                errors.Add($"Trunk name {trunk.Name} is used twice.");
            if (HostRegex().IsMatch(trunk.Host) == false)
                errors.Add($"{label}: host must be a domain name or an IP address.");
            if (trunk.Port is < 1 or > 65535)
                errors.Add($"{label}: port must be between 1 and 65535.");
            if (Regex.IsMatch(trunk.Username, "^[A-Za-z0-9+._-]{1,60}$") == false)
                errors.Add($"{label}: user name: letters, digits and + . _ - only.");
            CheckSecret(trunk.Password, $"{label}: password", errors, minLength: 1);
            if (string.IsNullOrEmpty(trunk.CallerId) == false && Regex.IsMatch(trunk.CallerId, @"^\+?[0-9]{3,15}$") == false)
                errors.Add($"{label}: caller number must be 3-15 digits.");
            CheckTarget(trunk.InboundTarget, null, $"{label}: inbound target", numbers, errors);
            CheckCodecs(trunk.Codecs, label, errors);
        }

        foreach (var route in package.OutboundRoutes)
        {
            var label = $"Outbound route {route.Pattern}";
            if (PatternRegex().IsMatch(route.Pattern) == false)
                errors.Add($"Outbound pattern '{route.Pattern}' may hold digits and X Z N . ! [ ] * # + - only.");
            if (trunkNames.Contains(route.Trunk) == false)
                errors.Add($"{label}: trunk '{route.Trunk}' does not exist.");
            if (route.StripDigits is < 0 or > 10)
                errors.Add($"{label}: stripped digits must be between 0 and 10.");
            if (string.IsNullOrEmpty(route.Prefix) == false && Regex.IsMatch(route.Prefix, @"^\+?[0-9]{1,15}$") == false)
                errors.Add($"{label}: prefix must be digits.");
            if (numbers.ContainsKey(route.Pattern))
                errors.Add($"{label}: the pattern is the same as an internal number.");
        }

        if (package.Extensions.Count == 0)
            result.Warnings.Add("No extensions yet - no phone can register.");

        return result;
    }

    private static void ValidateSip(AsteriskConfigurationPackage package, AsteriskValidationResult result)
    {
        var sip = package.Sip;
        if (IPAddress.TryParse(sip.BindAddress, out _) == false)
            result.Errors.Add("SIP bind address must be an IP address (0.0.0.0 = every interface).");
        if (sip.UdpPort is < 1 or > 65535)
            result.Errors.Add("SIP port must be between 1 and 65535.");
        if (string.IsNullOrEmpty(sip.ExternalAddress) == false && HostRegex().IsMatch(sip.ExternalAddress) == false)
            result.Errors.Add("External address must be a domain name or an IP address.");
        foreach (var network in sip.LocalNetworks)
        {
            var parts = network.Split('/');
            if (parts.Length != 2 || IPAddress.TryParse(parts[0], out _) == false || int.TryParse(parts[1], out var bits) == false || bits is < 0 or > 128)
                result.Errors.Add($"Local network '{network}' must look like 192.168.1.0/24.");
        }

        if (string.IsNullOrEmpty(sip.ExternalAddress) == false && sip.LocalNetworks.Count == 0)
            result.Warnings.Add("External address without local networks: phones in the LAN would also be sent the external address.");
        if (sip.Codecs.Count == 0)
            result.Errors.Add("Choose at least one codec.");
        CheckCodecs(sip.Codecs, "SIP", result.Errors);
        if (sip.QualifySeconds != 0 && sip.QualifySeconds is < 5 or > 3600)
            result.Errors.Add("Phone check interval must be 0 (off) or between 5 and 3600 seconds.");
        if (package.WebRtc.Enabled && package.WebRtc.HttpPort == sip.UdpPort && sip.TcpEnabled)
            result.Errors.Add("WebRTC HTTP port and the SIP TCP port cannot be the same.");
    }

    /// <summary>A chain of no-answer targets that comes back to its start would ring for ever.</summary>
    private static void CheckNoAnswerLoops(AsteriskConfigurationPackage package, List<string> errors)
    {
        var next = new Dictionary<string, string>();
        foreach (var extension in package.Extensions.Where(x => string.IsNullOrEmpty(x.NoAnswerTarget) == false))
            next.TryAdd(extension.Number, extension.NoAnswerTarget!);
        foreach (var group in package.RingGroups.Where(x => string.IsNullOrEmpty(x.NoAnswerTarget) == false))
            next.TryAdd(group.Number, group.NoAnswerTarget!);

        foreach (var start in next.Keys)
        {
            var current = start;
            for (var hop = 0; hop <= next.Count; hop++)
            {
                if (next.TryGetValue(current, out var target) == false)
                    break;
                if (target == start)
                {
                    errors.Add($"No-answer targets form a loop starting at {start}.");
                    break;
                }

                current = target;
            }
        }
    }

    private static bool IsPort(int port) => port is >= 1024 and <= 65535;

    private static void CheckSecret(string? value, string label, List<string> errors, int minLength = MinPasswordLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length < minLength)
        {
            errors.Add(minLength > 1 ? $"{label} must have at least {minLength} characters." : $"{label} is required.");
            return;
        }

        if (value.Length > 64 || SecretRegex().IsMatch(value) == false)
            errors.Add($"{label}: up to 64 letters, digits and !#$%&*+,./:=?@^_~- (no spaces, quotes, ';' or brackets).");
    }

    private static void CheckDisplayName(string? value, string label, List<string> errors)
    {
        if (value == null)
            return;
        if (value.Length > 40 || DisplayNameRegex().IsMatch(value) == false)
            errors.Add($"{label}: name may hold up to 40 letters, digits, spaces and . _ ( ) / + -");
    }

    private static void CheckCodecs(List<string> codecs, string label, List<string> errors)
    {
        foreach (var codec in codecs)
        {
            if (CodecRegex().IsMatch(codec) == false)
                errors.Add($"{label}: codec '{codec}' is not a codec name (e.g. alaw, ulaw, g722, opus).");
        }
    }

    private static void CheckTarget(string? target, string? own, string label, Dictionary<string, string> numbers, List<string> errors)
    {
        if (string.IsNullOrEmpty(target))
            return;
        if (numbers.ContainsKey(target) == false)
            errors.Add($"{label} {target} is neither an extension nor a ring group.");
        else if (target == own)
            errors.Add($"{label} points at itself.");
    }
}
