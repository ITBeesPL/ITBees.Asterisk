using System.Text;

namespace ITBees.Asterisk.Configuration;

/// <summary>
/// Turns a package into the Asterisk configuration files the library owns. The files are written whole: the PBX is
/// managed from the package, hand edits belong in the optional <c>*_custom.conf</c> files included at the end.
/// </summary>
public static class AsteriskConfigRenderer
{
    /// <summary>First line of every managed file; a file without it is backed up before the first overwrite.</summary>
    public const string ManagedMarker = "; Managed by ITBees.Asterisk - do not edit, changes are overwritten.";

    public const string PjsipFile = "pjsip.conf";
    public const string ExtensionsFile = "extensions.conf";
    public const string ManagerFile = "manager.conf";
    public const string RtpFile = "rtp.conf";
    public const string HttpFile = "http.conf";

    public const string InternalContext = "itbees-internal";
    public const string AutoAnswerContext = "itbees-autoanswer";
    public const string UdpTransport = "transport-udp";
    public const string TcpTransport = "transport-tcp";
    public const string WsTransport = "transport-ws";
    public const string WssTransport = "transport-wss";

    /// <summary>Private key and self-signed certificate of the HTTPS server, in one file (made by the host).</summary>
    public const string TlsCertificateFile = "keys/itbees-pbx.pem";

    /// <summary>File name → content, in the order the files are written.</summary>
    public static IReadOnlyDictionary<string, string> Render(AsteriskConfigurationPackage package)
    {
        var validation = package.Validate();
        if (validation.IsValid == false)
            throw new AsteriskConfigurationException("Invalid Asterisk configuration: " + string.Join(" | ", validation.Errors));

        return new Dictionary<string, string>
        {
            [RtpFile] = RenderRtp(package),
            [HttpFile] = RenderHttp(package),
            [ManagerFile] = RenderManager(package),
            [PjsipFile] = RenderPjsip(package),
            [ExtensionsFile] = RenderExtensions(package),
        };
    }

    public static string TrunkEndpoint(AsteriskTrunk trunk) => "trunk-" + trunk.Name;

    public static string TrunkContext(AsteriskTrunk trunk) => "itbees-from-trunk-" + trunk.Name;

    public static string HotlineContext(AsteriskExtension extension) => "itbees-hotline-" + extension.Number;

    /// <summary>
    /// The transport sections of a rendered pjsip.conf. Asterisk does not re-bind transports on a reload, so a change
    /// here needs a restart of the service.
    /// </summary>
    public static string TransportFingerprint(string? pjsipConf)
    {
        if (string.IsNullOrEmpty(pjsipConf))
            return string.Empty;
        var fingerprint = new StringBuilder();
        var inTransport = false;
        foreach (var raw in pjsipConf.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
                inTransport = line.StartsWith("[transport-", StringComparison.Ordinal);
            if (inTransport && line.Length > 0)
                fingerprint.Append(line).Append('\n');
        }

        return fingerprint.ToString();
    }

    private static StringBuilder NewFile(string what)
    {
        var sb = new StringBuilder();
        sb.Append(ManagedMarker).Append('\n');
        sb.Append("; ").Append(what).Append('\n').Append('\n');
        return sb;
    }

    private static string RenderRtp(AsteriskConfigurationPackage package)
    {
        var sb = NewFile("RTP (voice) port range.");
        sb.Append("[general]\n");
        sb.Append("rtpstart=").Append(package.Rtp.PortStart).Append('\n');
        sb.Append("rtpend=").Append(package.Rtp.PortEnd).Append('\n');
        return sb.ToString();
    }

    private static string RenderHttp(AsteriskConfigurationPackage package)
    {
        var sb = NewFile("Built-in HTTP server: SIP over WebSocket for browser phones (ws://host:port/ws).");
        sb.Append("[general]\n");
        sb.Append("enabled=").Append(package.WebRtc.Enabled ? "yes" : "no").Append('\n');
        if (package.WebRtc.Enabled)
        {
            sb.Append("bindaddr=").Append(package.WebRtc.BindAddress).Append('\n');
            sb.Append("bindport=").Append(package.WebRtc.HttpPort).Append('\n');
            if (package.WebRtc.TlsEnabled)
            {
                // wss:// for phones inside an https page (a browser refuses ws:// there).
                sb.Append("tlsenable=yes\n");
                sb.Append("tlsbindaddr=").Append(FormatBind(package.WebRtc.BindAddress)).Append(':').Append(package.WebRtc.TlsPort).Append('\n');
                sb.Append("tlscertfile=/etc/asterisk/").Append(TlsCertificateFile).Append('\n');
                sb.Append("tlsprivatekey=/etc/asterisk/").Append(TlsCertificateFile).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string RenderManager(AsteriskConfigurationPackage package)
    {
        var manager = package.Manager;
        var sb = NewFile("Asterisk Manager Interface: the account ITBees.Asterisk uses for status and call control.");
        sb.Append("[general]\n");
        sb.Append("enabled=yes\n");
        sb.Append("webenabled=no\n");
        sb.Append("port=").Append(manager.Port).Append('\n');
        sb.Append("bindaddr=").Append(manager.BindAddress).Append('\n');
        sb.Append('\n');
        sb.Append('[').Append(manager.Username).Append("]\n");
        sb.Append("secret=").Append(manager.Password).Append('\n');
        if (System.Net.IPAddress.TryParse(manager.BindAddress, out var bind) && System.Net.IPAddress.IsLoopback(bind))
        {
            sb.Append("deny=0.0.0.0/0.0.0.0\n");
            sb.Append("permit=127.0.0.1/255.255.255.255\n");
        }

        sb.Append("read=all\n");
        sb.Append("write=all\n");
        return sb.ToString();
    }

    private static string RenderPjsip(AsteriskConfigurationPackage package)
    {
        var sip = package.Sip;
        var sb = NewFile("SIP: transports, extensions (phones) and trunks.");

        sb.Append("[global]\n");
        sb.Append("type=global\n");
        sb.Append("user_agent=ITBees-Asterisk\n");
        sb.Append('\n');

        AppendTransport(sb, UdpTransport, "udp", $"{FormatBind(sip.BindAddress)}:{sip.UdpPort}", sip);
        if (sip.TcpEnabled)
            AppendTransport(sb, TcpTransport, "tcp", $"{FormatBind(sip.BindAddress)}:{sip.UdpPort}", sip);
        if (package.WebRtc.Enabled)
        {
            // The WebSocket itself is served by http.conf; this transport only makes PJSIP accept it.
            AppendTransport(sb, WsTransport, "ws", FormatBind(package.WebRtc.BindAddress), sip);
            if (package.WebRtc.TlsEnabled)
                AppendTransport(sb, WssTransport, "wss", FormatBind(package.WebRtc.BindAddress), sip);
        }

        foreach (var extension in package.Extensions.Where(x => x.Enabled))
            AppendExtension(sb, package, extension);

        foreach (var trunk in package.Trunks.Where(x => x.Enabled))
            AppendTrunk(sb, package, trunk);

        sb.Append("#tryinclude pjsip_custom.conf\n");
        return sb.ToString();
    }

    private static void AppendTransport(StringBuilder sb, string name, string protocol, string bind, SipSettings sip)
    {
        sb.Append('[').Append(name).Append("]\n");
        sb.Append("type=transport\n");
        sb.Append("protocol=").Append(protocol).Append('\n');
        sb.Append("bind=").Append(bind).Append('\n');
        if (string.IsNullOrEmpty(sip.ExternalAddress) == false)
        {
            sb.Append("external_media_address=").Append(sip.ExternalAddress).Append('\n');
            sb.Append("external_signaling_address=").Append(sip.ExternalAddress).Append('\n');
            foreach (var network in sip.LocalNetworks)
                sb.Append("local_net=").Append(network).Append('\n');
        }

        sb.Append('\n');
    }

    private static void AppendExtension(StringBuilder sb, AsteriskConfigurationPackage package, AsteriskExtension extension)
    {
        var number = extension.Number;
        var webRtc = extension.DeviceModel == AsteriskDeviceModel.WebRtcBrowser;
        var codecs = extension.Codecs.Count > 0 ? extension.Codecs : package.Sip.Codecs;
        var context = string.IsNullOrEmpty(extension.HotlineTarget) ? InternalContext : HotlineContext(extension);

        sb.Append("; ").Append(number).Append(' ').Append(Clean(extension.Name)).Append(" (").Append(extension.DeviceModel).Append(")\n");
        sb.Append('[').Append(number).Append("]\n");
        sb.Append("type=endpoint\n");
        sb.Append("context=").Append(context).Append('\n');
        sb.Append("disallow=all\n");
        sb.Append("allow=").Append(string.Join(',', codecs)).Append('\n');
        sb.Append("auth=").Append(number).Append("-auth\n");
        sb.Append("aors=").Append(number).Append('\n');
        sb.Append("callerid=\"").Append(Clean(extension.Name)).Append("\" <").Append(number).Append(">\n");
        sb.Append("dtmf_mode=").Append(DtmfMode(extension.DtmfMode)).Append('\n');
        sb.Append("direct_media=no\n");
        sb.Append("rtp_symmetric=yes\n");
        sb.Append("force_rport=yes\n");
        sb.Append("rewrite_contact=yes\n");
        sb.Append("language=pl\n");
        if (webRtc)
        {
            // DTLS-SRTP, ICE and the bundled media a browser insists on; the certificate is made by Asterisk itself.
            sb.Append("webrtc=yes\n");
            sb.Append("dtls_auto_generate_cert=yes\n");
        }

        sb.Append('\n');

        sb.Append('[').Append(number).Append("-auth]\n");
        sb.Append("type=auth\n");
        sb.Append("auth_type=userpass\n");
        sb.Append("username=").Append(number).Append('\n');
        sb.Append("password=").Append(extension.Password).Append('\n');
        sb.Append('\n');

        sb.Append('[').Append(number).Append("]\n");
        sb.Append("type=aor\n");
        sb.Append("max_contacts=").Append(extension.MaxContacts).Append('\n');
        sb.Append("remove_existing=yes\n");
        if (package.Sip.QualifySeconds > 0)
            sb.Append("qualify_frequency=").Append(package.Sip.QualifySeconds).Append('\n');
        sb.Append('\n');
    }

    private static void AppendTrunk(StringBuilder sb, AsteriskConfigurationPackage package, AsteriskTrunk trunk)
    {
        var name = TrunkEndpoint(trunk);
        var server = trunk.Port == 5060 ? trunk.Host : $"{trunk.Host}:{trunk.Port}";
        var codecs = trunk.Codecs.Count > 0 ? trunk.Codecs : package.Sip.Codecs;

        sb.Append("; trunk ").Append(trunk.Name).Append('\n');
        sb.Append('[').Append(name).Append("-auth]\n");
        sb.Append("type=auth\n");
        sb.Append("auth_type=userpass\n");
        sb.Append("username=").Append(trunk.Username).Append('\n');
        sb.Append("password=").Append(trunk.Password).Append('\n');
        sb.Append('\n');

        sb.Append('[').Append(name).Append("-aor]\n");
        sb.Append("type=aor\n");
        sb.Append("contact=sip:").Append(server).Append('\n');
        sb.Append("qualify_frequency=60\n");
        sb.Append('\n');

        sb.Append('[').Append(name).Append("]\n");
        sb.Append("type=endpoint\n");
        sb.Append("transport=").Append(UdpTransport).Append('\n');
        sb.Append("context=").Append(TrunkContext(trunk)).Append('\n');
        sb.Append("disallow=all\n");
        sb.Append("allow=").Append(string.Join(',', codecs)).Append('\n');
        sb.Append("outbound_auth=").Append(name).Append("-auth\n");
        sb.Append("aors=").Append(name).Append("-aor\n");
        sb.Append("from_user=").Append(trunk.Username).Append('\n');
        sb.Append("from_domain=").Append(trunk.Host).Append('\n');
        sb.Append("direct_media=no\n");
        sb.Append("rtp_symmetric=yes\n");
        sb.Append("force_rport=yes\n");
        sb.Append("rewrite_contact=yes\n");
        sb.Append('\n');

        sb.Append('[').Append(name).Append("-identify]\n");
        sb.Append("type=identify\n");
        sb.Append("endpoint=").Append(name).Append('\n');
        sb.Append("match=").Append(trunk.Host).Append('\n');
        sb.Append('\n');

        if (trunk.Register)
        {
            sb.Append('[').Append(name).Append("-reg]\n");
            sb.Append("type=registration\n");
            sb.Append("transport=").Append(UdpTransport).Append('\n');
            sb.Append("outbound_auth=").Append(name).Append("-auth\n");
            sb.Append("server_uri=sip:").Append(server).Append('\n');
            sb.Append("client_uri=sip:").Append(trunk.Username).Append('@').Append(server).Append('\n');
            sb.Append("contact_user=").Append(trunk.Username).Append('\n');
            sb.Append("retry_interval=60\n");
            sb.Append("expiration=300\n");
            sb.Append('\n');
        }
    }

    private static string RenderExtensions(AsteriskConfigurationPackage package)
    {
        var dialplan = package.Dialplan;
        var sb = NewFile("Dialplan: who can call whom.");
        sb.Append("[general]\n");
        sb.Append("static=yes\n");
        sb.Append("writeprotect=yes\n");
        sb.Append("clearglobalvars=no\n");
        sb.Append('\n');
        sb.Append("[globals]\n");
        sb.Append('\n');

        // Runs on the called channel just before it is rung: the headers make Fanvil and Yealink devices answer at once.
        sb.Append('[').Append(AutoAnswerContext).Append("]\n");
        sb.Append("exten => addheader,1,Set(PJSIP_HEADER(add,Call-Info)=<sip:itbees>\\;answer-after=0)\n");
        sb.Append(" same => n,Set(PJSIP_HEADER(add,Alert-Info)=<http://itbees>\\;info=alert-autoanswer\\;delay=0)\n");
        sb.Append(" same => n,Return()\n");
        sb.Append('\n');

        sb.Append('[').Append(InternalContext).Append("]\n");
        var enabled = package.Extensions.Where(x => x.Enabled).ToList();
        foreach (var extension in enabled)
        {
            var ring = dialplan.RingSeconds;
            sb.Append("; ").Append(extension.Number).Append(' ').Append(Clean(extension.Name)).Append('\n');
            sb.Append("exten => ").Append(extension.Number).Append(",1,NoOp(Call to ").Append(extension.Number).Append(")\n");
            sb.Append(" same => n,").Append(Dial("PJSIP/" + extension.Number, ring, extension.AutoAnswer)).Append('\n');
            AppendAfterDial(sb, package, extension.NoAnswerTarget);

            if (string.IsNullOrEmpty(dialplan.IntercomPrefix) == false)
            {
                sb.Append("exten => ").Append(dialplan.IntercomPrefix).Append(extension.Number)
                    .Append(",1,NoOp(Intercom to ").Append(extension.Number).Append(")\n");
                sb.Append(" same => n,").Append(Dial("PJSIP/" + extension.Number, ring, autoAnswer: true)).Append('\n');
                sb.Append(" same => n,Hangup()\n");
            }
        }

        foreach (var group in package.RingGroups)
        {
            var members = group.Members.Where(m => enabled.Any(x => x.Number == m)).ToList();
            var ring = group.RingSeconds > 0 ? group.RingSeconds : dialplan.RingSeconds;
            sb.Append("; group ").Append(group.Number).Append(' ').Append(Clean(group.Name)).Append('\n');
            sb.Append("exten => ").Append(group.Number).Append(",1,NoOp(Call to group ").Append(group.Number).Append(")\n");
            if (members.Count > 0 && group.Strategy == AsteriskRingStrategy.RingAll)
            {
                sb.Append(" same => n,").Append(Dial(string.Join('&', members.Select(m => "PJSIP/" + m)), ring, false)).Append('\n');
            }
            else
            {
                foreach (var member in members)
                    sb.Append(" same => n,").Append(Dial("PJSIP/" + member, ring, false)).Append('\n');
            }

            AppendAfterDial(sb, package, group.NoAnswerTarget);
        }

        foreach (var route in package.OutboundRoutes)
        {
            var trunk = package.Trunks.FirstOrDefault(x => x.Enabled && string.Equals(x.Name, route.Trunk, StringComparison.OrdinalIgnoreCase));
            if (trunk == null)
                continue;
            var number = route.StripDigits > 0 ? $"${{EXTEN:{route.StripDigits}}}" : "${EXTEN}";
            sb.Append("; outside through ").Append(trunk.Name).Append('\n');
            sb.Append("exten => _").Append(route.Pattern).Append(",1,NoOp(Outbound ${EXTEN} via ").Append(trunk.Name).Append(")\n");
            if (string.IsNullOrEmpty(trunk.CallerId) == false)
                sb.Append(" same => n,Set(CALLERID(num)=").Append(trunk.CallerId).Append(")\n");
            sb.Append(" same => n,Dial(PJSIP/").Append(route.Prefix).Append(number).Append('@').Append(TrunkEndpoint(trunk)).Append(",60)\n");
            sb.Append(" same => n,Hangup()\n");
        }

        sb.Append('\n');

        foreach (var extension in enabled.Where(x => string.IsNullOrEmpty(x.HotlineTarget) == false))
        {
            sb.Append("; whatever ").Append(extension.Number).Append(" dials reaches ").Append(extension.HotlineTarget).Append('\n');
            sb.Append('[').Append(HotlineContext(extension)).Append("]\n");
            AppendCatchAll(sb, $"Goto({InternalContext},{extension.HotlineTarget},1)");
            sb.Append('\n');
        }

        foreach (var trunk in package.Trunks.Where(x => x.Enabled))
        {
            sb.Append('[').Append(TrunkContext(trunk)).Append("]\n");
            AppendCatchAll(sb, string.IsNullOrEmpty(trunk.InboundTarget)
                ? "Hangup(21)"
                : $"Goto({InternalContext},{trunk.InboundTarget},1)");
            sb.Append('\n');
        }

        sb.Append("#tryinclude extensions_custom.conf\n");
        return sb.ToString();
    }

    private static void AppendAfterDial(StringBuilder sb, AsteriskConfigurationPackage package, string? noAnswerTarget)
    {
        // Reached only when the call was not answered: an answered call ends with the callee's hangup.
        var targetExists = string.IsNullOrEmpty(noAnswerTarget) == false &&
                           (package.Extensions.Any(x => x.Enabled && x.Number == noAnswerTarget) ||
                            package.RingGroups.Any(x => x.Number == noAnswerTarget));
        sb.Append(targetExists ? $" same => n,Goto({InternalContext},{noAnswerTarget},1)\n" : " same => n,Hangup()\n");
    }

    private static void AppendCatchAll(StringBuilder sb, string application)
    {
        // Digits, "+48..." from an operator, feature codes and a call without any number.
        foreach (var pattern in new[] { "_X!", "_+X!", "_*X!", "_#X!", "s" })
            sb.Append("exten => ").Append(pattern).Append(",1,").Append(application).Append('\n');
    }

    private static string Dial(string target, int ringSeconds, bool autoAnswer) => autoAnswer
        ? $"Dial({target},{ringSeconds},b({AutoAnswerContext}^addheader^1))"
        : $"Dial({target},{ringSeconds})";

    private static string DtmfMode(AsteriskDtmfMode mode) => mode switch
    {
        AsteriskDtmfMode.Info => "info",
        AsteriskDtmfMode.Inband => "inband",
        AsteriskDtmfMode.Auto => "auto",
        _ => "rfc4733",
    };

    private static string FormatBind(string address) => address.Contains(':') ? $"[{address}]" : address;

    /// <summary>Second line of defence after the validator: nothing that ends a line or opens a section or comment.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\r' or '\n' or ';' or '[' or ']' or '"' or '<' or '>' or '\\')
                continue;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
