using System.Globalization;
using ITBees.Asterisk.Ami;

namespace ITBees.Asterisk.Status;

/// <summary>Live state of the PBX read over AMI.</summary>
public sealed class AsteriskStatus
{
    public string? Version { get; set; }

    /// <summary>Start of the Asterisk process, in the host's local time as Asterisk reports it ("2026-11-19 08:15:02").</summary>
    public string? StartedAt { get; set; }

    public string? ReloadedAt { get; set; }

    public int CurrentCalls { get; set; }

    public List<AsteriskEndpointStatus> Endpoints { get; set; } = new();

    public List<AsteriskChannelStatus> Channels { get; set; } = new();

    public List<AsteriskRegistrationStatus> Registrations { get; set; } = new();
}

/// <summary>An extension or a trunk as Asterisk sees it.</summary>
public sealed class AsteriskEndpointStatus
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Not in use", "In use", "Ringing", "Unavailable" (nothing registered)...</summary>
    public string? DeviceState { get; set; }

    public int ActiveChannels { get; set; }

    /// <summary>True when at least one device is registered (or, for a trunk, its static contact answers).</summary>
    public bool Registered => Contacts.Count > 0 && Contacts.Any(x => x.Reachable != false);

    public List<AsteriskContactStatus> Contacts { get; set; } = new();
}

/// <summary>One registered device of an extension.</summary>
public sealed class AsteriskContactStatus
{
    public string? Uri { get; set; }

    /// <summary>Address the registration came from, e.g. "192.168.1.57".</summary>
    public string? Address { get; set; }

    /// <summary>What the device calls itself, e.g. "Fanvil X3SP 2.4.4" or "Yealink SIP-T30P 124.86.0.40".</summary>
    public string? UserAgent { get; set; }

    /// <summary>Reachable / Unreachable / NonQualified / Unknown.</summary>
    public string? Status { get; set; }

    /// <summary>Null when the PBX does not ping the device.</summary>
    public bool? Reachable { get; set; }

    public double? RoundtripMs { get; set; }
}

public sealed class AsteriskChannelStatus
{
    /// <summary>Asterisk channel name, e.g. "PJSIP/100-00000012" - what Hangup takes.</summary>
    public string Channel { get; set; } = string.Empty;

    public string? State { get; set; }

    public string? CallerNumber { get; set; }

    public string? CallerName { get; set; }

    public string? ConnectedNumber { get; set; }

    public string? DialedNumber { get; set; }

    public int DurationSeconds { get; set; }

    public string? BridgeId { get; set; }
}

/// <summary>Registration of a trunk at its operator.</summary>
public sealed class AsteriskRegistrationStatus
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Registered", "Unregistered", "Rejected"...</summary>
    public string? Status { get; set; }

    public string? ServerUri { get; set; }
}

public static class AsteriskStatusReader
{
    public static async Task<AsteriskStatus> ReadAsync(IAmiConnection ami, CancellationToken cancellationToken = default)
    {
        var status = new AsteriskStatus();

        var settings = await ami.SendAsync("CoreSettings", null, cancellationToken);
        status.Version = settings.Response["AsteriskVersion"];

        var core = await ami.SendAsync("CoreStatus", null, cancellationToken);
        status.StartedAt = Join(core.Response["CoreStartupDate"], core.Response["CoreStartupTime"]);
        status.ReloadedAt = Join(core.Response["CoreReloadDate"], core.Response["CoreReloadTime"]);
        status.CurrentCalls = ParseInt(core.Response["CoreCurrentCalls"]);

        var endpoints = await TryList(ami, "PJSIPShowEndpoints", cancellationToken);
        foreach (var e in endpoints.Where(x => x.Event == "EndpointList"))
        {
            status.Endpoints.Add(new AsteriskEndpointStatus
            {
                Name = e["ObjectName"] ?? string.Empty,
                DeviceState = e["DeviceState"],
                ActiveChannels = ParseInt(e["ActiveChannels"]),
            });
        }

        var contacts = await TryList(ami, "PJSIPShowContacts", cancellationToken);
        foreach (var c in contacts.Where(x => x.Event == "ContactList"))
        {
            // "Aor" for a registered phone; for older versions the object name is "<aor>/<uri>".
            var owner = c["EndpointName"];
            if (string.IsNullOrEmpty(owner))
                owner = c["Aor"] ?? c["ObjectName"]?.Split('/')[0];
            var endpoint = status.Endpoints.FirstOrDefault(x => x.Name == owner) ??
                           status.Endpoints.FirstOrDefault(x => x.Name == c["Aor"]) ??
                           status.Endpoints.FirstOrDefault(x => x.Name + "-aor" == c["Aor"]);
            if (endpoint == null)
                continue;

            var contactStatus = c["Status"];
            var roundtrip = ParseDouble(c["RoundtripUsec"]);
            endpoint.Contacts.Add(new AsteriskContactStatus
            {
                Uri = c["Uri"],
                Address = HostOf(c["ViaAddress"]) ?? HostOfUri(c["Uri"]),
                UserAgent = string.IsNullOrEmpty(c["UserAgent"]) ? null : c["UserAgent"],
                Status = contactStatus,
                Reachable = contactStatus switch
                {
                    "Reachable" or "Created" => true,
                    "Unreachable" or "Removed" => false,
                    _ => null,
                },
                RoundtripMs = roundtrip is > 0 ? Math.Round(roundtrip.Value / 1000.0, 1) : null,
            });
        }

        var channels = await TryList(ami, "CoreShowChannels", cancellationToken);
        foreach (var ch in channels.Where(x => x.Event == "CoreShowChannel"))
        {
            status.Channels.Add(new AsteriskChannelStatus
            {
                Channel = ch["Channel"] ?? string.Empty,
                State = ch["ChannelStateDesc"],
                CallerNumber = ch["CallerIDNum"],
                CallerName = ch["CallerIDName"],
                ConnectedNumber = ch["ConnectedLineNum"],
                DialedNumber = ch["Exten"],
                DurationSeconds = ParseDuration(ch["Duration"]),
                BridgeId = string.IsNullOrEmpty(ch["BridgeId"]) ? null : ch["BridgeId"],
            });
        }

        var registrations = await TryList(ami, "PJSIPShowRegistrationsOutbound", cancellationToken);
        foreach (var r in registrations.Where(x => x.Event == "OutboundRegistrationDetail"))
        {
            status.Registrations.Add(new AsteriskRegistrationStatus
            {
                Name = r["ObjectName"] ?? string.Empty,
                Status = r["Status"],
                ServerUri = r["ServerUri"],
            });
        }

        return status;
    }

    /// <summary>List actions answer with an error when there is nothing to list ("No endpoints found").</summary>
    private static async Task<IReadOnlyList<AmiMessage>> TryList(IAmiConnection ami, string action, CancellationToken cancellationToken)
    {
        try
        {
            return (await ami.SendAsync(action, null, cancellationToken)).Events;
        }
        catch (AsteriskActionException)
        {
            return Array.Empty<AmiMessage>();
        }
    }

    private static string? Join(string? date, string? time) =>
        string.IsNullOrEmpty(date) ? null : string.IsNullOrEmpty(time) ? date : $"{date} {time}";

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>"00:01:25" → 85.</summary>
    private static int ParseDuration(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? (int)parsed.TotalSeconds : 0;

    private static string? HostOf(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return null;
        var colon = address.LastIndexOf(':');
        return colon > 0 && address.Contains(']') == false ? address[..colon] : address;
    }

    private static string? HostOfUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return null;
        var at = uri.IndexOf('@');
        var host = at >= 0 ? uri[(at + 1)..] : uri.Replace("sip:", string.Empty);
        var end = host.IndexOfAny(new[] { ':', ';', '>' });
        return end > 0 ? host[..end] : host;
    }
}
