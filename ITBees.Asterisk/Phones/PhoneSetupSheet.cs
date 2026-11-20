using ITBees.Asterisk.Configuration;

namespace ITBees.Asterisk.Phones;

public sealed class PhoneSetupField
{
    public PhoneSetupField(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Name of the setting, close to what the device's web page calls it.</summary>
    public string Name { get; }

    public string Value { get; }
}

/// <summary>
/// What an installer types into a phone so that it registers with an extension of the package: the values, where
/// to find them in the device's web interface and device-specific hints.
/// </summary>
public sealed class PhoneSetupSheet
{
    public string ExtensionNumber { get; set; } = string.Empty;

    public AsteriskDeviceModel DeviceModel { get; set; }

    /// <summary>Human name of the device, e.g. "Fanvil X3SP".</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Menu path of the SIP account page in the device's web interface.</summary>
    public string? MenuPath { get; set; }

    public List<PhoneSetupField> Fields { get; set; } = new();

    public List<string> Hints { get; set; } = new();

    /// <summary>
    /// Builds the sheet for one extension. <paramref name="pbxAddress"/> is the address the phone reaches the PBX at
    /// (the LAN address of the host that runs Asterisk).
    /// </summary>
    public static PhoneSetupSheet For(AsteriskConfigurationPackage package, AsteriskExtension extension, string pbxAddress)
    {
        var sheet = new PhoneSetupSheet
        {
            ExtensionNumber = extension.Number,
            DeviceModel = extension.DeviceModel,
            DeviceName = NameOf(extension.DeviceModel),
        };
        var port = package.Sip.UdpPort.ToString();
        var codecs = string.Join(", ", (extension.Codecs.Count > 0 ? extension.Codecs : package.Sip.Codecs).Select(CodecName));
        var dtmf = extension.DtmfMode switch
        {
            AsteriskDtmfMode.Info => "SIP INFO",
            AsteriskDtmfMode.Inband => "In-band",
            AsteriskDtmfMode.Auto => "Auto",
            _ => "RFC2833",
        };

        switch (extension.DeviceModel)
        {
            case AsteriskDeviceModel.FanvilPa2s:
            case AsteriskDeviceModel.FanvilX3sp:
                sheet.MenuPath = "Line → SIP → Line 1";
                sheet.Fields.Add(new("Username", extension.Number));
                sheet.Fields.Add(new("Display name", extension.Name));
                sheet.Fields.Add(new("Authentication name", extension.Number));
                sheet.Fields.Add(new("Authentication password", extension.Password));
                sheet.Fields.Add(new("Server address", pbxAddress));
                sheet.Fields.Add(new("Server port", port));
                sheet.Fields.Add(new("Transport protocol", "UDP"));
                sheet.Fields.Add(new("Activate", "yes"));
                sheet.Fields.Add(new("DTMF type (Line → SIP → Advanced settings)", dtmf));
                sheet.Fields.Add(new("Codecs (Line → SIP → Codecs settings)", codecs));
                sheet.Hints.Add("The device's web page: http://<device address>, factory login admin / admin - change the password.");
                break;

            case AsteriskDeviceModel.YealinkT30p:
                sheet.MenuPath = "Account → Register → Account 1";
                sheet.Fields.Add(new("Line active", "Enabled"));
                sheet.Fields.Add(new("Label", extension.Name));
                sheet.Fields.Add(new("Display name", extension.Name));
                sheet.Fields.Add(new("Register name", extension.Number));
                sheet.Fields.Add(new("User name", extension.Number));
                sheet.Fields.Add(new("Password", extension.Password));
                sheet.Fields.Add(new("SIP server 1 - Server host", pbxAddress));
                sheet.Fields.Add(new("SIP server 1 - Port", port));
                sheet.Fields.Add(new("Transport", "UDP"));
                sheet.Fields.Add(new("DTMF type (Account → Advanced)", dtmf));
                sheet.Fields.Add(new("Codecs (Account → Codec)", codecs));
                sheet.Hints.Add("The phone's web page: http://<phone address> (OK button on the phone shows the address), factory login admin / admin - change the password.");
                break;

            case AsteriskDeviceModel.WebRtcBrowser:
                sheet.MenuPath = null;
                sheet.Fields.Add(new("WebSocket server", $"ws://{pbxAddress}:{package.WebRtc.HttpPort}/ws"));
                if (package.WebRtc.TlsEnabled)
                    sheet.Fields.Add(new("WebSocket server (https pages)", $"wss://{pbxAddress}:{package.WebRtc.TlsPort}/ws"));
                sheet.Fields.Add(new("SIP URI", $"sip:{extension.Number}@{pbxAddress}"));
                sheet.Fields.Add(new("User name", extension.Number));
                sheet.Fields.Add(new("Password", extension.Password));
                sheet.Hints.Add("A browser gives a page the microphone only on https:// or http://localhost.");
                if (package.WebRtc.TlsEnabled)
                    sheet.Hints.Add($"The wss:// certificate is self-signed: open https://{pbxAddress}:{package.WebRtc.TlsPort}/ws in the browser once and accept it.");
                break;

            default:
                sheet.Fields.Add(new("SIP server / registrar", pbxAddress));
                sheet.Fields.Add(new("Port", port));
                sheet.Fields.Add(new("Transport", "UDP"));
                sheet.Fields.Add(new("User name / authentication name", extension.Number));
                sheet.Fields.Add(new("Password", extension.Password));
                sheet.Fields.Add(new("Display name", extension.Name));
                sheet.Fields.Add(new("DTMF", dtmf));
                sheet.Fields.Add(new("Codecs", codecs));
                break;
        }

        if (extension.DeviceModel == AsteriskDeviceModel.FanvilPa2s)
        {
            sheet.Hints.Add(string.IsNullOrEmpty(extension.HotlineTarget)
                ? "Call button: set the number it dials in the device (function key / hotline), e.g. a ring group of the PBX."
                : $"Call button: the PBX sends every call from this device to {extension.HotlineTarget}, so any number programmed under the button works.");
            sheet.Hints.Add("Relay (gate / door): the code that switches it is set in the device; the PBX passes the keys pressed during a call as " + dtmf + ".");
        }

        if (extension.AutoAnswer)
            sheet.Hints.Add("Calls to this extension carry an auto-answer header - the device picks up by itself.");
        else if (string.IsNullOrEmpty(package.Dialplan.IntercomPrefix) == false && extension.DeviceModel != AsteriskDeviceModel.WebRtcBrowser)
            sheet.Hints.Add($"Dialling {package.Dialplan.IntercomPrefix}{extension.Number} from another phone makes this device answer at once (intercom).");

        if (string.IsNullOrEmpty(extension.Mac) == false)
            sheet.Hints.Add("Device MAC: " + extension.Mac);

        return sheet;
    }

    public static string NameOf(AsteriskDeviceModel model) => model switch
    {
        AsteriskDeviceModel.FanvilPa2s => "Fanvil PA2S",
        AsteriskDeviceModel.FanvilX3sp => "Fanvil X3SP",
        AsteriskDeviceModel.YealinkT30p => "Yealink SIP-T30P",
        AsteriskDeviceModel.WebRtcBrowser => "Browser (WebRTC)",
        _ => "SIP device",
    };

    private static string CodecName(string codec) => codec switch
    {
        "alaw" => "G.711A (PCMA)",
        "ulaw" => "G.711U (PCMU)",
        "g722" => "G.722",
        "g729" => "G.729",
        "opus" => "Opus",
        _ => codec,
    };
}
