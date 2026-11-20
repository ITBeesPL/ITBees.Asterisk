using System.Text.Json;
using System.Text.Json.Serialization;

namespace ITBees.Asterisk.Configuration;

/// <summary>
/// Desired state of the PBX: everything the library writes to the Asterisk configuration files. Applying the same
/// package twice changes nothing the second time.
/// </summary>
public sealed class AsteriskConfigurationPackage
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Format version of the package (for future migrations of stored JSON).</summary>
    public int Version { get; set; } = 1;

    public SipSettings Sip { get; set; } = new();

    public RtpSettings Rtp { get; set; } = new();

    public WebRtcSettings WebRtc { get; set; } = new();

    public ManagerSettings Manager { get; set; } = new();

    public DialplanSettings Dialplan { get; set; } = new();

    public List<AsteriskExtension> Extensions { get; set; } = new();

    public List<AsteriskRingGroup> RingGroups { get; set; } = new();

    public List<AsteriskTrunk> Trunks { get; set; } = new();

    public List<AsteriskOutboundRoute> OutboundRoutes { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AsteriskConfigurationPackage FromJson(string json) =>
        JsonSerializer.Deserialize<AsteriskConfigurationPackage>(json, JsonOptions) ?? new AsteriskConfigurationPackage();

    public AsteriskValidationResult Validate() => AsteriskConfigurationValidator.Validate(this);
}

/// <summary>SIP signalling of the PBX (PJSIP transports and defaults of every endpoint).</summary>
public sealed class SipSettings
{
    /// <summary>Address the PBX listens on; 0.0.0.0 = every interface of the host.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    public int UdpPort { get; set; } = 5060;

    /// <summary>Also listen on TCP (same port). Hardware phones on a LAN work over UDP.</summary>
    public bool TcpEnabled { get; set; }

    /// <summary>Public address of the host when phones or a trunk reach the PBX through NAT. Empty = no NAT.</summary>
    public string? ExternalAddress { get; set; }

    /// <summary>Networks reached without NAT, e.g. "192.168.1.0/24". Only used with <see cref="ExternalAddress"/>.</summary>
    public List<string> LocalNetworks { get; set; } = new();

    /// <summary>Codecs offered by default, in order of preference.</summary>
    public List<string> Codecs { get; set; } = new() { "alaw", "ulaw" };

    /// <summary>Seconds between OPTIONS pings of a registered phone (reachability and round trip time). 0 = off.</summary>
    public int QualifySeconds { get; set; } = 30;
}

public sealed class RtpSettings
{
    public int PortStart { get; set; } = 10000;

    public int PortEnd { get; set; } = 10200;
}

/// <summary>
/// SIP over WebSocket for browser phones (WebRTC): the built-in HTTP server of Asterisk plus a "ws" transport.
/// </summary>
public sealed class WebRtcSettings
{
    public bool Enabled { get; set; }

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port of the Asterisk HTTP server; the phone connects to ws://host:port/ws.</summary>
    public int HttpPort { get; set; } = 8088;

    /// <summary>
    /// Also serve the WebSocket over TLS (wss://host:TlsPort/ws). A page opened over https:// may only use wss://,
    /// so a phone inside an https web application needs it. The certificate is self-signed and made on the host;
    /// each browser has to accept it once (open https://host:TlsPort/ws and confirm).
    /// </summary>
    public bool TlsEnabled { get; set; } = true;

    public int TlsPort { get; set; } = 8089;
}

/// <summary>Asterisk Manager Interface account the library itself uses for status and call control.</summary>
public sealed class ManagerSettings
{
    /// <summary>Address AMI listens on. Loopback: only the host that runs the library can connect.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5038;

    public string Username { get; set; } = "itbees";

    public string Password { get; set; } = string.Empty;
}

public sealed class DialplanSettings
{
    /// <summary>How long a called phone rings before the call moves on (no-answer target) or ends.</summary>
    public int RingSeconds { get; set; } = 30;

    /// <summary>
    /// Prefix that calls an extension with auto-answer (intercom / paging), e.g. *80100 talks through the speaker of
    /// extension 100 at once. Empty = no intercom prefix.
    /// </summary>
    public string? IntercomPrefix { get; set; } = "*80";
}

/// <summary>Physical or software device behind an extension; selects defaults and the setup sheet.</summary>
public enum AsteriskDeviceModel
{
    Generic = 0,

    /// <summary>Fanvil PA2S SIP paging gateway / intercom (speaker out, microphone, call button, relay).</summary>
    FanvilPa2s = 1,

    /// <summary>Fanvil X3SP desk phone (PoE).</summary>
    FanvilX3sp = 2,

    /// <summary>Yealink SIP-T30P desk phone (PoE).</summary>
    YealinkT30p = 3,

    /// <summary>Phone in a browser (WebRTC over WebSocket), e.g. the kiosk screen of a parking column.</summary>
    WebRtcBrowser = 4,
}

public enum AsteriskDtmfMode
{
    /// <summary>RTP events (RFC 4733 / 2833) - what door-opening codes of Fanvil intercoms expect.</summary>
    Rfc4733 = 0,
    Info = 1,
    Inband = 2,
    Auto = 3,
}

/// <summary>One SIP account: a phone, an intercom or a browser registers with it.</summary>
public sealed class AsteriskExtension
{
    /// <summary>Internal number and SIP user name, digits only.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Shown as the caller name.</summary>
    public string Name { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public AsteriskDeviceModel DeviceModel { get; set; } = AsteriskDeviceModel.Generic;

    public bool Enabled { get; set; } = true;

    public AsteriskDtmfMode DtmfMode { get; set; } = AsteriskDtmfMode.Rfc4733;

    /// <summary>Own codec list; empty = <see cref="SipSettings.Codecs"/>.</summary>
    public List<string> Codecs { get; set; } = new();

    /// <summary>How many devices may be registered with this account at once (the newest replaces the oldest).</summary>
    public int MaxContacts { get; set; } = 1;

    /// <summary>
    /// Every call to this extension is answered by the device at once (speaker of a PA2S, intercom panel).
    /// </summary>
    public bool AutoAnswer { get; set; }

    /// <summary>
    /// Hotline: whatever this device dials, the call goes to this number (extension or ring group). For an intercom
    /// with a single call button, so the button works regardless of what is programmed in the device.
    /// </summary>
    public string? HotlineTarget { get; set; }

    /// <summary>Extension or ring group called when this one does not answer, is busy or is not registered.</summary>
    public string? NoAnswerTarget { get; set; }

    /// <summary>MAC address of the device (informational, shown on the setup sheet).</summary>
    public string? Mac { get; set; }

    public string? Note { get; set; }
}

public enum AsteriskRingStrategy
{
    /// <summary>All members ring at the same time; the first to answer takes the call.</summary>
    RingAll = 0,

    /// <summary>Members ring one after another in the listed order.</summary>
    Sequential = 1,
}

/// <summary>A number that rings several extensions.</summary>
public sealed class AsteriskRingGroup
{
    public string Number { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Extension numbers.</summary>
    public List<string> Members { get; set; } = new();

    public AsteriskRingStrategy Strategy { get; set; } = AsteriskRingStrategy.RingAll;

    /// <summary>Ring time (per member for <see cref="AsteriskRingStrategy.Sequential"/>); 0 = <see cref="DialplanSettings.RingSeconds"/>.</summary>
    public int RingSeconds { get; set; }

    /// <summary>Extension or ring group called when nobody in the group answers.</summary>
    public string? NoAnswerTarget { get; set; }
}

/// <summary>SIP account at an operator (or another PBX) for calls to and from the outside.</summary>
public sealed class AsteriskTrunk
{
    /// <summary>Short name used in the configuration: letters, digits, '-' and '_'.</summary>
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5060;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Register at the operator (most consumer and business SIP accounts need it).</summary>
    public bool Register { get; set; } = true;

    /// <summary>Number presented on outgoing calls; empty = the account's user name.</summary>
    public string? CallerId { get; set; }

    /// <summary>Extension or ring group that receives calls coming in over this trunk. Empty = incoming calls are rejected.</summary>
    public string? InboundTarget { get; set; }

    public List<string> Codecs { get; set; } = new();

    public bool Enabled { get; set; } = true;
}

/// <summary>Which dialled numbers leave through which trunk.</summary>
public sealed class AsteriskOutboundRoute
{
    /// <summary>Asterisk pattern without the leading underscore, e.g. "0X." (0 + anything) or "XXXXXXXXX" (nine digits).</summary>
    public string Pattern { get; set; } = string.Empty;

    public string Trunk { get; set; } = string.Empty;

    /// <summary>Leading digits removed before the number is sent to the trunk (e.g. 1 for the "0" of "0X.").</summary>
    public int StripDigits { get; set; }

    /// <summary>Digits put in front of the number sent to the trunk.</summary>
    public string? Prefix { get; set; }
}
