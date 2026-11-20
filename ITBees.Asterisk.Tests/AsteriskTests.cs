using ITBees.Asterisk.Ami;
using ITBees.Asterisk.Configuration;
using ITBees.Asterisk.Host;
using ITBees.Asterisk.Phones;
using ITBees.Asterisk.Provisioning;

namespace ITBees.Asterisk.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Sample_package_is_valid_and_survives_json()
    {
        var package = TestData.Package();
        Assert.Empty(package.Validate().Errors);

        var copy = AsteriskConfigurationPackage.FromJson(package.ToJson());
        Assert.Equal(AsteriskConfigRenderer.Render(package), AsteriskConfigRenderer.Render(copy));
        Assert.Contains("\"deviceModel\":\"FanvilPa2s\"", package.ToJson());
    }

    [Theory]
    [InlineData("secret with space")]
    [InlineData("semi;colon12")]
    [InlineData("line\nbreak123")]
    [InlineData("bracket[123]")]
    [InlineData("short")]
    public void Passwords_that_could_break_a_config_line_are_refused(string password)
    {
        var package = TestData.Package();
        package.Extensions[0].Password = password;
        Assert.Contains(package.Validate().Errors, e => e.Contains("Extension 100: password"));
        Assert.Throws<AsteriskConfigurationException>(() => AsteriskConfigRenderer.Render(package));
    }

    [Fact]
    public void Names_cannot_inject_sections()
    {
        var package = TestData.Package();
        package.Extensions[0].Name = "x]\n[evil";
        Assert.False(package.Validate().IsValid);
    }

    [Fact]
    public void Duplicate_numbers_unknown_members_and_loops_are_errors()
    {
        var package = TestData.Package();
        package.RingGroups.Add(new AsteriskRingGroup { Number = "100", Name = "dup", Members = { "999" } });
        var errors = package.Validate().Errors;
        Assert.Contains(errors, e => e.Contains("Number 100 is used twice"));
        Assert.Contains(errors, e => e.Contains("member 999"));

        package = TestData.Package();
        package.Extensions[0].NoAnswerTarget = "101";
        package.Extensions[1].NoAnswerTarget = "100";
        Assert.Contains(package.Validate().Errors, e => e.Contains("loop"));
    }

    [Fact]
    public void Browser_phone_needs_webrtc()
    {
        var package = TestData.Package();
        package.Extensions.Add(new AsteriskExtension { Number = "300", Name = "Kiosk", Password = "Secret-300x", DeviceModel = AsteriskDeviceModel.WebRtcBrowser });
        Assert.Contains(package.Validate().Errors, e => e.Contains("WebRTC"));
        package.WebRtc.Enabled = true;
        Assert.True(package.Validate().IsValid);
    }

    [Fact]
    public void Route_needs_an_existing_trunk()
    {
        var package = TestData.Package();
        package.OutboundRoutes.Add(new AsteriskOutboundRoute { Pattern = "0X.", Trunk = "operator", StripDigits = 1 });
        Assert.Contains(package.Validate().Errors, e => e.Contains("trunk 'operator'"));
    }
}

public class RendererTests
{
    [Fact]
    public void Pjsip_has_transport_endpoint_auth_and_aor_per_extension()
    {
        var pjsip = AsteriskConfigRenderer.Render(TestData.Package())[AsteriskConfigRenderer.PjsipFile];

        Assert.StartsWith(AsteriskConfigRenderer.ManagedMarker, pjsip);
        Assert.Contains("[transport-udp]\ntype=transport\nprotocol=udp\nbind=0.0.0.0:5060\n", pjsip);
        Assert.Contains("[100]\ntype=endpoint\ncontext=itbees-internal\ndisallow=all\nallow=alaw,ulaw\nauth=100-auth\naors=100\ncallerid=\"Kasa\" <100>\ndtmf_mode=rfc4733\n", pjsip);
        Assert.Contains("[100-auth]\ntype=auth\nauth_type=userpass\nusername=100\npassword=Secret-100x\n", pjsip);
        Assert.Contains("[100]\ntype=aor\nmax_contacts=1\nremove_existing=yes\nqualify_frequency=30\n", pjsip);
        // The intercom with a hotline lands in its own context.
        Assert.Contains("[200]\ntype=endpoint\ncontext=itbees-hotline-200\n", pjsip);
        Assert.DoesNotContain("transport-ws", pjsip);
        Assert.DoesNotContain("\r", pjsip);
    }

    [Fact]
    public void Disabled_extension_is_left_out_everywhere()
    {
        var package = TestData.Package();
        package.Extensions[1].Enabled = false;
        var files = AsteriskConfigRenderer.Render(package);
        Assert.DoesNotContain("[101]", files[AsteriskConfigRenderer.PjsipFile]);
        Assert.Contains("exten => 600,1,NoOp(Call to group 600)\n same => n,Dial(PJSIP/100,30)\n", files[AsteriskConfigRenderer.ExtensionsFile]);
    }

    [Fact]
    public void Dialplan_has_extensions_intercom_group_and_hotline()
    {
        var package = TestData.Package();
        package.Extensions[0].NoAnswerTarget = "101";
        var dialplan = AsteriskConfigRenderer.Render(package)[AsteriskConfigRenderer.ExtensionsFile];

        Assert.Contains("[itbees-internal]\n", dialplan);
        Assert.Contains("exten => 100,1,NoOp(Call to 100)\n same => n,Dial(PJSIP/100,30)\n same => n,Goto(itbees-internal,101,1)\n", dialplan);
        Assert.Contains("exten => 101,1,NoOp(Call to 101)\n same => n,Dial(PJSIP/101,30)\n same => n,Hangup()\n", dialplan);
        Assert.Contains("exten => *80100,1,NoOp(Intercom to 100)\n same => n,Dial(PJSIP/100,30,b(itbees-autoanswer^addheader^1))\n", dialplan);
        Assert.Contains("exten => 600,1,NoOp(Call to group 600)\n same => n,Dial(PJSIP/100&PJSIP/101,30)\n same => n,Hangup()\n", dialplan);
        Assert.Contains("[itbees-hotline-200]\nexten => _X!,1,Goto(itbees-internal,600,1)\n", dialplan);
        Assert.Contains("exten => s,1,Goto(itbees-internal,600,1)\n", dialplan);
        Assert.Contains("answer-after=0", dialplan);
    }

    [Fact]
    public void Sequential_group_rings_members_in_order()
    {
        var package = TestData.Package();
        package.RingGroups[0].Strategy = AsteriskRingStrategy.Sequential;
        package.RingGroups[0].RingSeconds = 10;
        var dialplan = AsteriskConfigRenderer.Render(package)[AsteriskConfigRenderer.ExtensionsFile];
        Assert.Contains(" same => n,Dial(PJSIP/100,10)\n same => n,Dial(PJSIP/101,10)\n same => n,Hangup()\n", dialplan);
    }

    [Fact]
    public void Trunk_with_route_and_inbound_target()
    {
        var package = TestData.Package();
        package.Trunks.Add(new AsteriskTrunk
        {
            Name = "operator", Host = "sip.example.com", Username = "48521234567", Password = "TrunkPass1", CallerId = "48521234567", InboundTarget = "600",
        });
        package.OutboundRoutes.Add(new AsteriskOutboundRoute { Pattern = "0X.", Trunk = "operator", StripDigits = 1 });
        Assert.Empty(package.Validate().Errors);
        var files = AsteriskConfigRenderer.Render(package);
        var pjsip = files[AsteriskConfigRenderer.PjsipFile];
        var dialplan = files[AsteriskConfigRenderer.ExtensionsFile];

        Assert.Contains("[trunk-operator-reg]\ntype=registration\n", pjsip);
        Assert.Contains("server_uri=sip:sip.example.com\nclient_uri=sip:48521234567@sip.example.com\n", pjsip);
        Assert.Contains("[trunk-operator-identify]\ntype=identify\nendpoint=trunk-operator\nmatch=sip.example.com\n", pjsip);
        Assert.Contains("exten => _0X.,1,NoOp(Outbound ${EXTEN} via operator)\n same => n,Set(CALLERID(num)=48521234567)\n same => n,Dial(PJSIP/${EXTEN:1}@trunk-operator,60)\n", dialplan);
        Assert.Contains("[itbees-from-trunk-operator]\nexten => _X!,1,Goto(itbees-internal,600,1)\n", dialplan);
    }

    [Fact]
    public void WebRtc_adds_http_server_ws_transport_and_dtls()
    {
        var package = TestData.Package();
        package.WebRtc.Enabled = true;
        package.Extensions.Add(new AsteriskExtension { Number = "300", Name = "Kiosk", Password = "Secret-300x", DeviceModel = AsteriskDeviceModel.WebRtcBrowser });
        var files = AsteriskConfigRenderer.Render(package);

        Assert.Contains("enabled=yes\nbindaddr=0.0.0.0\nbindport=8088\n", files[AsteriskConfigRenderer.HttpFile]);
        Assert.Contains("[transport-ws]\ntype=transport\nprotocol=ws\nbind=0.0.0.0\n", files[AsteriskConfigRenderer.PjsipFile]);
        Assert.Contains("webrtc=yes\ndtls_auto_generate_cert=yes\n", files[AsteriskConfigRenderer.PjsipFile]);
    }

    [Fact]
    public void Manager_is_limited_to_loopback()
    {
        var manager = AsteriskConfigRenderer.Render(TestData.Package())[AsteriskConfigRenderer.ManagerFile];
        Assert.Contains("[general]\nenabled=yes\nwebenabled=no\nport=5038\nbindaddr=127.0.0.1\n", manager);
        Assert.Contains("[itbees]\nsecret=ManagerSecret1\ndeny=0.0.0.0/0.0.0.0\npermit=127.0.0.1/255.255.255.255\n", manager);
    }

    [Fact]
    public void Transport_fingerprint_ignores_extensions()
    {
        var package = TestData.Package();
        var before = AsteriskConfigRenderer.TransportFingerprint(AsteriskConfigRenderer.Render(package)[AsteriskConfigRenderer.PjsipFile]);
        package.Extensions[0].Password = "Another-Secret1";
        var samePort = AsteriskConfigRenderer.TransportFingerprint(AsteriskConfigRenderer.Render(package)[AsteriskConfigRenderer.PjsipFile]);
        package.Sip.UdpPort = 5080;
        var otherPort = AsteriskConfigRenderer.TransportFingerprint(AsteriskConfigRenderer.Render(package)[AsteriskConfigRenderer.PjsipFile]);

        Assert.Equal(before, samePort);
        Assert.NotEqual(before, otherPort);
    }
}

public class AmiConnectionTests
{
    [Fact]
    public async Task Login_list_and_command()
    {
        await using var server = new FakeAmiServer();
        await using var ami = await AmiConnection.ConnectAsync(new AmiConnectionOptions { Port = server.Port, Username = "itbees", Password = server.Password });

        Assert.StartsWith("Asterisk Call Manager", ami.Banner);

        var endpoints = await ami.SendAsync("PJSIPShowEndpoints");
        Assert.Equal(new[] { "100", "101" }, endpoints.Events.Select(e => e["ObjectName"]));

        var command = await ami.SendAsync("Command", new[] { new KeyValuePair<string, string>("Command", "core show version") });
        Assert.Equal(new[] { "line one", "line two" }, command.Response.GetAll("Output"));

        var error = await Assert.ThrowsAsync<AsteriskActionException>(() => ami.SendAsync("Nonsense"));
        Assert.Contains("Invalid/unknown command", error.Message);
    }

    [Fact]
    public async Task Wrong_password_is_an_authentication_error()
    {
        await using var server = new FakeAmiServer();
        await Assert.ThrowsAsync<AsteriskAuthenticationException>(() =>
            AmiConnection.ConnectAsync(new AmiConnectionOptions { Port = server.Port, Username = "itbees", Password = "wrong" }));
    }

    [Fact]
    public async Task Nothing_listening_is_a_connection_error()
    {
        int port;
        await using (var server = new FakeAmiServer())
            port = server.Port;
        await Assert.ThrowsAsync<AsteriskConnectionException>(() =>
            AmiConnection.ConnectAsync(new AmiConnectionOptions { Port = port, Username = "itbees", Password = "x", ConnectTimeout = TimeSpan.FromSeconds(1) }));
    }
}

public class ProvisionerTests
{
    private static AsteriskProvisioner Provisioner(FakeHost host, params string[] endpoints) => new(host)
    {
        AmiFactory = (_, _) => Task.FromResult<IAmiConnection>(FakeAmi.WithEndpoints(endpoints)),
        StartupPollInterval = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task First_apply_writes_everything_and_restarts_then_second_changes_nothing()
    {
        var host = new FakeHost();
        host.Files["modules.conf"] = "[modules]\nautoload=yes\n";
        var provisioner = Provisioner(host, "100", "101", "200");
        var package = TestData.Package();

        var first = await provisioner.ApplyAsync(package);

        Assert.True(first.Success, first.Summary);
        Assert.True(first.Restarted);
        Assert.Equal(3, first.LoadedEndpoints);
        Assert.Contains("noload => chan_sip.so", host.Files["modules.conf"]);
        Assert.Contains("service restart", host.Calls);
        Assert.Equal(6, host.Calls.Count(c => c.StartsWith("write ")));
        // Results never carry passwords.
        Assert.DoesNotContain(first.Changes, c => c.Contains("Secret"));

        host.Calls.Clear();
        var second = await provisioner.ApplyAsync(package);

        Assert.True(second.Success);
        Assert.Empty(second.Changes);
        Assert.False(second.Restarted);
        Assert.Empty(host.Calls);
        Assert.Equal("Already up to date.", second.Summary);
    }

    [Fact]
    public async Task Changing_an_extension_only_reloads()
    {
        var host = new FakeHost();
        var package = TestData.Package();
        await Provisioner(host, "100", "101", "200").ApplyAsync(package);
        host.Calls.Clear();

        package.Extensions.Add(new AsteriskExtension { Number = "102", Name = "Nowy", Password = "Secret-102x" });
        var result = await Provisioner(host, "100", "101", "102", "200").ApplyAsync(package);

        Assert.True(result.Success, result.Summary);
        Assert.True(result.Reloaded);
        Assert.False(result.Restarted);
        Assert.Contains("cli core reload", host.Calls);
        Assert.DoesNotContain("service restart", host.Calls);
    }

    [Fact]
    public async Task Changing_the_sip_port_restarts()
    {
        var host = new FakeHost();
        var package = TestData.Package();
        await Provisioner(host, "100", "101", "200").ApplyAsync(package);
        package.Sip.UdpPort = 5080;

        var result = await Provisioner(host, "100", "101", "200").ApplyAsync(package);

        Assert.True(result.Restarted);
    }

    [Fact]
    public async Task Plan_writes_nothing()
    {
        var host = new FakeHost();
        var result = await Provisioner(host).ApplyAsync(TestData.Package(), new AsteriskProvisioningOptions { DryRun = true });

        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.NotEmpty(result.Changes);
        Assert.Empty(host.Calls);
        Assert.Empty(host.Files);
    }

    [Fact]
    public async Task Missing_asterisk_fails_unless_install_is_allowed()
    {
        var host = new FakeHost { Installation = new AsteriskInstallation() };
        var refused = await Provisioner(host).ApplyAsync(TestData.Package());
        Assert.False(refused.Success);
        Assert.Contains("not installed", refused.Summary);

        var installed = await Provisioner(host, "100", "101", "200").ApplyAsync(TestData.Package(), new AsteriskProvisioningOptions { InstallIfMissing = true });
        Assert.True(installed.Success, installed.Summary);
        Assert.Contains("install", host.Calls);
        Assert.Contains("service enable", host.Calls);
    }

    [Fact]
    public async Task Fewer_loaded_accounts_than_expected_fails_the_check()
    {
        var host = new FakeHost();
        var provisioner = Provisioner(host, "100");
        var result = await provisioner.ApplyAsync(TestData.Package(), new AsteriskProvisioningOptions { StartupWait = TimeSpan.FromMilliseconds(50) });

        Assert.False(result.Success);
        Assert.Contains("1 of 3", result.Summary);
    }

    [Fact]
    public async Task Invalid_package_touches_nothing()
    {
        var host = new FakeHost();
        var package = TestData.Package();
        package.Manager.Password = "";
        var result = await Provisioner(host).ApplyAsync(package);

        Assert.False(result.Success);
        Assert.Empty(host.Calls);
    }
}

public class HostTests
{
    [Fact]
    public async Task Installation_reads_version_and_service_state()
    {
        var runner = new FakeRunner
        {
            Answer = c => c.EndsWith("-V") ? new SystemCommandResult(0, "Asterisk 20.6.0~dfsg+~cs6.13.40431414-2build5\n", "")
                : c.StartsWith("systemctl is-active") ? new SystemCommandResult(3, "inactive\n", "")
                : new SystemCommandResult(0, "", ""),
        };
        var installation = await new AsteriskHost(runner).GetInstallationAsync();

        Assert.True(installation.Installed);
        Assert.StartsWith("20.6.0", installation.Version);
        Assert.False(installation.ServiceActive);
        Assert.Equal("inactive", installation.ServiceState);
    }

    [Fact]
    public async Task Missing_binary_means_not_installed()
    {
        var runner = new FakeRunner { Answer = _ => new SystemCommandResult(-1, "", "not found", started: false) };
        Assert.False((await new AsteriskHost(runner).GetInstallationAsync()).Installed);
    }

    [Fact]
    public async Task Write_backs_up_a_foreign_file_and_restricts_access()
    {
        var runner = new FakeRunner
        {
            Answer = c => c.Contains(" cat ") ? new SystemCommandResult(0, "; distribution sample\n", "") : new SystemCommandResult(0, "", ""),
        };
        var host = new AsteriskHost(runner, new AsteriskHostOptions { ConfigDirectory = "/nonexistent-itbees-test" });

        await host.WriteConfigAsync("pjsip.conf", "content\r\n");

        Assert.Contains("sudo -n cp -n -p /nonexistent-itbees-test/pjsip.conf /nonexistent-itbees-test/pjsip.conf.itbees-orig", runner.Commands);
        var tee = runner.Commands.IndexOf("sudo -n tee /nonexistent-itbees-test/pjsip.conf");
        Assert.True(tee >= 0);
        Assert.Equal("content\n", runner.Inputs[tee]);
        Assert.Contains("sudo -n chmod 640 /nonexistent-itbees-test/pjsip.conf", runner.Commands);
    }

    [Fact]
    public async Task File_names_cannot_leave_the_directory()
    {
        var host = new AsteriskHost(new FakeRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => host.ReadConfigAsync("../shadow"));
    }

    [Fact]
    public async Task Missing_package_is_explained_when_building_is_switched_off()
    {
        var runner = new FakeRunner
        {
            Answer = c => c.Contains("install") ? new SystemCommandResult(100, "", "E: Unable to locate package asterisk") : new SystemCommandResult(0, "", ""),
        };
        var host = new AsteriskHost(runner, new AsteriskHostOptions { BuildFromSourceWhenNoPackage = false });
        var error = await Assert.ThrowsAsync<AsteriskHostException>(() => host.InstallAsync());
        Assert.Contains("no 'asterisk' package", error.Message);
    }

    [Fact]
    public async Task Missing_package_falls_back_to_a_build_from_the_sources()
    {
        var built = false;
        var runner = new FakeRunner();
        runner.Answer = c =>
        {
            // Debian 12 says it on stdout: "Package asterisk is not available ... E: Package 'asterisk' has no installation candidate".
            if (c.Contains("apt-get install")) return new SystemCommandResult(100, "E: Package 'asterisk' has no installation candidate", "");
            if (c.Contains("bash -s")) built = true;
            if (c.EndsWith("-V")) return built ? new SystemCommandResult(0, "Asterisk 22.12.0\n", "") : new SystemCommandResult(-1, "", "", started: false);
            return new SystemCommandResult(0, "", "");
        };
        var log = Path.Combine(Path.GetTempPath(), "itbees-asterisk-test-" + Guid.NewGuid().ToString("N") + ".log");
        var host = new AsteriskHost(runner, new AsteriskHostOptions { InstallLogPath = log });

        var result = await host.InstallAsync();

        Assert.Contains("22.12.0", result);
        Assert.Contains("official sources", result);
        var build = runner.Commands.FindIndex(c => c.Contains("bash -s"));
        Assert.Equal($"sudo -n bash -c bash -s > '{log}' 2>&1", runner.Commands[build]);
        // The script itself travels on stdin, with Unix line ends.
        Assert.StartsWith("#!/bin/bash\n", runner.Inputs[build]);
        Assert.DoesNotContain("\r", runner.Inputs[build]);
        Assert.Contains("make install", runner.Inputs[build]);
    }

    [Fact]
    public async Task Install_progress_is_the_last_step_line_of_the_build_log()
    {
        var log = Path.Combine(Path.GetTempPath(), "itbees-asterisk-test-" + Guid.NewGuid().ToString("N") + ".log");
        var host = new AsteriskHost(new FakeRunner(), new AsteriskHostOptions { InstallLogPath = log });
        Assert.Null(await host.GetInstallProgressAsync());
        try
        {
            await File.WriteAllTextAsync(log, "[itbees] 12:00:01 1/7 installing build tools\nReading package lists...\n[itbees] 12:01:44 5/7 compiling with 4 job(s) - the longest step\n   [CC] chan_pjsip.c\n");
            Assert.Equal("5/7 compiling with 4 job(s) - the longest step (since 12:01:44)", await host.GetInstallProgressAsync());
        }
        finally
        {
            File.Delete(log);
        }
    }
}

public class ServiceTests
{
    [Fact]
    public async Task Status_maps_endpoints_contacts_and_channels()
    {
        var ami = new FakeAmi((action, _) => action switch
        {
            "CoreSettings" => new AmiResponse(FakeAmi.Message(("Response", "Success"), ("AsteriskVersion", "20.6.0")), Array.Empty<AmiMessage>()),
            "CoreStatus" => new AmiResponse(FakeAmi.Message(("Response", "Success"), ("CoreStartupDate", "2026-11-19"), ("CoreStartupTime", "08:00:00"), ("CoreCurrentCalls", "1")), Array.Empty<AmiMessage>()),
            "PJSIPShowEndpoints" => FakeAmi.Ok(
                FakeAmi.Message(("Event", "EndpointList"), ("ObjectName", "100"), ("DeviceState", "In use"), ("ActiveChannels", "1")),
                FakeAmi.Message(("Event", "EndpointList"), ("ObjectName", "101"), ("DeviceState", "Unavailable"), ("ActiveChannels", "0"))),
            "PJSIPShowContacts" => FakeAmi.Ok(
                FakeAmi.Message(("Event", "ContactList"), ("Aor", "100"), ("Uri", "sip:100@192.168.1.57:5060"), ("UserAgent", "Yealink SIP-T30P 124.86.0.40"),
                    ("Status", "Reachable"), ("RoundtripUsec", "12345"), ("ViaAddress", "192.168.1.57:5060"), ("EndpointName", "100"))),
            "CoreShowChannels" => FakeAmi.Ok(
                FakeAmi.Message(("Event", "CoreShowChannel"), ("Channel", "PJSIP/100-00000001"), ("ChannelStateDesc", "Up"), ("CallerIDNum", "100"),
                    ("Exten", "600"), ("Duration", "00:01:25"))),
            _ => throw new AsteriskActionException(action, "No registrations found"),
        });
        var service = new AsteriskService(new FakeHost()) { AmiFactory = (_, _) => Task.FromResult<IAmiConnection>(ami) };

        var status = await service.GetStatusAsync(TestData.Package().Manager);

        Assert.Equal("20.6.0", status.Version);
        Assert.Equal("2026-11-19 08:00:00", status.StartedAt);
        Assert.Equal(1, status.CurrentCalls);
        var registered = status.Endpoints.Single(x => x.Name == "100");
        Assert.True(registered.Registered);
        Assert.Equal("192.168.1.57", registered.Contacts[0].Address);
        Assert.Equal(12.3, registered.Contacts[0].RoundtripMs);
        Assert.False(status.Endpoints.Single(x => x.Name == "101").Registered);
        Assert.Equal(85, status.Channels.Single().DurationSeconds);
        Assert.Empty(status.Registrations);
    }

    [Fact]
    public async Task Status_reads_contacts_as_a_live_asterisk_22_sends_them()
    {
        // Field names and values copied from a 22.12.0 with a Fanvil PA2 and a browser phone registered.
        var ami = new FakeAmi((action, _) => action switch
        {
            "PJSIPShowEndpoints" => FakeAmi.Ok(
                FakeAmi.Message(("Event", "EndpointList"), ("ObjectName", "100"), ("Aor", "100"), ("DeviceState", "Unavailable"), ("ActiveChannels", "0")),
                FakeAmi.Message(("Event", "EndpointList"), ("ObjectName", "101"), ("Aor", "101"), ("DeviceState", "Not in use"), ("ActiveChannels", "0"))),
            "PJSIPShowContacts" => FakeAmi.Ok(
                FakeAmi.Message(("Event", "ContactList"), ("ObjectType", "contact"), ("ObjectName", "100;@f57aa50d26267206498ac6994e38cb94"),
                    ("ViaAddr", "192.168.1.5"), ("Endpoint", "100"), ("ViaPort", "5060"), ("Uri", "sip:100@192.168.1.5:5060"),
                    ("UserAgent", "Fanvil PA2 2.8.2.7010 0c383e29ab17"), ("Status", "NonQualified"), ("RoundtripUsec", "N/A")),
                FakeAmi.Message(("Event", "ContactList"), ("ObjectType", "contact"), ("ObjectName", "101;@1fb147fee78f711841b3bbe5beb6f069"),
                    ("ViaAddr", "8oepein968g6.invalid"), ("Endpoint", "101"), ("ViaPort", "0"),
                    ("Uri", "sip:9drre02o@127.0.0.1:53686;transport=WS;x-ast-orig-host=8oepein968g6.invalid:0"),
                    ("UserAgent", "Octopark screen intercom"), ("Status", "Reachable"), ("RoundtripUsec", "8206"))),
            "CoreSettings" or "CoreStatus" => new AmiResponse(FakeAmi.Message(("Response", "Success")), Array.Empty<AmiMessage>()),
            _ => FakeAmi.Ok(),
        });
        var service = new AsteriskService(new FakeHost()) { AmiFactory = (_, _) => Task.FromResult<IAmiConnection>(ami) };

        var status = await service.GetStatusAsync(TestData.Package().Manager);

        var fanvil = status.Endpoints.Single(x => x.Name == "100");
        Assert.True(fanvil.Registered);
        Assert.Equal("192.168.1.5", fanvil.Contacts.Single().Address);
        Assert.Equal("Fanvil PA2 2.8.2.7010 0c383e29ab17", fanvil.Contacts.Single().UserAgent);
        Assert.Null(fanvil.Contacts.Single().RoundtripMs);
        var browser = status.Endpoints.Single(x => x.Name == "101");
        Assert.True(browser.Registered);
        Assert.Equal("127.0.0.1", browser.Contacts.Single().Address);
        Assert.Equal(8.2, browser.Contacts.Single().RoundtripMs);
    }

    [Fact]
    public async Task Originate_rings_the_first_phone_and_dials_from_the_internal_context()
    {
        var ami = new FakeAmi((_, _) => FakeAmi.Ok());
        var service = new AsteriskService(new FakeHost()) { AmiFactory = (_, _) => Task.FromResult<IAmiConnection>(ami) };

        await service.OriginateAsync(TestData.Package().Manager, "100", "200");

        var (action, fields) = ami.Sent.Single();
        Assert.Equal("Originate", action);
        Assert.Contains(new KeyValuePair<string, string>("Channel", "PJSIP/100"), fields);
        Assert.Contains(new KeyValuePair<string, string>("Context", "itbees-internal"), fields);
        Assert.Contains(new KeyValuePair<string, string>("Exten", "200"), fields);
        await Assert.ThrowsAsync<ArgumentException>(() => service.OriginateAsync(TestData.Package().Manager, "100", "200\r\nAction: Command"));
    }
}

public class PhoneSetupSheetTests
{
    [Fact]
    public void Sheets_carry_the_account_of_each_model()
    {
        var package = TestData.Package();

        var yealink = PhoneSetupSheet.For(package, package.Extensions[0], "192.168.1.10");
        Assert.Equal("Yealink SIP-T30P", yealink.DeviceName);
        Assert.Contains(yealink.Fields, f => f.Name == "SIP server 1 - Server host" && f.Value == "192.168.1.10");
        Assert.Contains(yealink.Fields, f => f.Name == "Password" && f.Value == "Secret-100x");

        var fanvil = PhoneSetupSheet.For(package, package.Extensions[2], "192.168.1.10");
        Assert.Equal("Fanvil PA2S", fanvil.DeviceName);
        Assert.Contains(fanvil.Fields, f => f.Name == "Server address" && f.Value == "192.168.1.10");
        Assert.Contains(fanvil.Hints, h => h.Contains("600"));

        package.WebRtc.Enabled = true;
        var browser = PhoneSetupSheet.For(package, new AsteriskExtension { Number = "300", Password = "Secret-300x", DeviceModel = AsteriskDeviceModel.WebRtcBrowser }, "192.168.1.10");
        Assert.Contains(browser.Fields, f => f.Value == "ws://192.168.1.10:8088/ws");
    }
}
