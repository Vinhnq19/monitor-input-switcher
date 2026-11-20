using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonitorInputSwitcher;

namespace MonitorInputSwitcher.Checks;

internal static class Program
{
    private const byte Dp = 0x0F;
    private const byte Hdmi1 = 0x11;
    private const byte Hdmi2 = 0x12;

    private static int _checks;
    private static int _failures;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
        {
            return RunProbe();
        }

        try
        {
            RunInputSwitchChecks();
            RunCapabilityChecks();
            RunSettingsChecks();
            RunHotkeyChecks();
            RunArgumentChecks();
            RunPairingInviteChecks();
        }
        catch (Exception ex)
        {
            Fail("unhandled exception", ex.ToString());
        }

        Console.WriteLine($"{_checks} assertions, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunInputSwitchChecks()
    {
        var dpToHdmiProfile = new InputProfile(Dp, Hdmi2, false);
        var reads = new ScriptedReads()
            .Add(Reading(Dp, Codes(Dp, Hdmi2)))
            .Add(Reading(Hdmi2, null));
        var writer = new RecordingWriter(new VcpWrite(true, null, null));
        var pauses = new List<int>();

        var result = InputSwitch.Execute(
            InputAction.Toggle,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch toggle DP to HDMI outcome", SwitchOutcome.Confirmed, result.Outcome);
        Equal<byte?>("input-switch toggle DP to HDMI requested", Hdmi2, result.Requested);
        Equal<uint?>("input-switch toggle DP to HDMI current", Hdmi2, result.Current);
        Check("input-switch toggle DP to HDMI has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckSingleWrite("input-switch toggle DP to HDMI", writer, Hdmi2);
        CheckReadPlan("input-switch toggle DP to HDMI", reads, 2);
        EqualSequence("input-switch toggle DP to HDMI pauses", new[] { 150 }, pauses);

        reads = new ScriptedReads()
            .Add(Reading(Hdmi2, Codes(Dp, Hdmi2)))
            .Add(Reading(Dp, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Toggle,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch toggle HDMI to DP outcome", SwitchOutcome.Confirmed, result.Outcome);
        Equal<byte?>("input-switch toggle HDMI to DP requested", Dp, result.Requested);
        Equal<uint?>("input-switch toggle HDMI to DP current", Dp, result.Current);
        Check("input-switch toggle HDMI to DP has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckSingleWrite("input-switch toggle HDMI to DP", writer, Dp);
        CheckReadPlan("input-switch toggle HDMI to DP", reads, 2);
        EqualSequence("input-switch toggle HDMI to DP pauses", new[] { 150 }, pauses);

        reads = new ScriptedReads()
            .Add(Reading(null, Codes(Dp)))
            .Add(Reading(Dp, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch direct PC with unknown current outcome", SwitchOutcome.Confirmed, result.Outcome);
        Equal<byte?>("input-switch direct PC with unknown current requested", Dp, result.Requested);
        Equal<uint?>("input-switch direct PC with unknown current readback", Dp, result.Current);
        CheckSingleWrite("input-switch direct PC with unknown current", writer, Dp);
        CheckReadPlan("input-switch direct PC with unknown current", reads, 2);

        reads = new ScriptedReads().Add(Reading(null, Codes(Dp, Hdmi2)));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Toggle,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch toggle with unknown current outcome", SwitchOutcome.NotAvailable, result.Outcome);
        Equal<byte?>("input-switch toggle with unknown current requested", null, result.Requested);
        Equal<uint?>("input-switch toggle with unknown current current", null, result.Current);
        Check("input-switch toggle with unknown current has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckNoWrites("input-switch toggle with unknown current", writer);
        CheckReadPlan("input-switch toggle with unknown current", reads, 1);

        reads = new ScriptedReads().Add(Reading(0x10u, Codes(Dp, Hdmi2, 0x10)));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Toggle,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch toggle outside pair outcome", SwitchOutcome.NotAvailable, result.Outcome);
        Equal<byte?>("input-switch toggle outside pair requested", null, result.Requested);
        Equal<uint?>("input-switch toggle outside pair current", 0x10u, result.Current);
        CheckNoWrites("input-switch toggle outside pair", writer);
        CheckReadPlan("input-switch toggle outside pair", reads, 1);

        reads = new ScriptedReads().Add(Reading(Hdmi2, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch unadvertised code outcome", SwitchOutcome.NotAvailable, result.Outcome);
        Equal<byte?>("input-switch unadvertised code requested", Dp, result.Requested);
        Equal<uint?>("input-switch unadvertised code current", Hdmi2, result.Current);
        Check("input-switch unadvertised code has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckNoWrites("input-switch unadvertised code", writer);
        CheckReadPlan("input-switch unadvertised code", reads, 1);

        reads = new ScriptedReads().Add(Reading(Hdmi2, Codes(Hdmi2)));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch advertised list without requested code outcome", SwitchOutcome.NotAvailable, result.Outcome);
        Equal<byte?>("input-switch advertised list without requested code requested", Dp, result.Requested);
        CheckNoWrites("input-switch advertised list without requested code", writer);

        var optInProfile = new InputProfile(Dp, Hdmi2, true);
        reads = new ScriptedReads()
            .Add(Reading(Hdmi1, null))
            .Add(Reading(Dp, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            optInProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch opt-in unverified write outcome", SwitchOutcome.Confirmed, result.Outcome);
        Equal<byte?>("input-switch opt-in unverified write requested", Dp, result.Requested);
        Equal<uint?>("input-switch opt-in unverified write current", Dp, result.Current);
        CheckSingleWrite("input-switch opt-in unverified write", writer, Dp);
        CheckReadPlan("input-switch opt-in unverified write", reads, 2);

        var installedProfile = new AppSettings().ToProfile();
        Equal<byte>("input-switch default installed PC code", Dp, installedProfile.Pc);
        Check("input-switch default installed profile has no laptop code", installedProfile.Laptop is null);

        reads = new ScriptedReads()
            .Add(Reading(Hdmi2, Codes(Dp)))
            .Add(Reading(Dp, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            installedProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch PC action before HDMI setup outcome", SwitchOutcome.Confirmed, result.Outcome);
        Equal<byte?>("input-switch PC action before HDMI setup requested", Dp, result.Requested);
        CheckSingleWrite("input-switch PC action before HDMI setup", writer, Dp);
        CheckReadPlan("input-switch PC action before HDMI setup", reads, 2);

        reads = new ScriptedReads();
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Laptop,
            installedProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch laptop before HDMI setup outcome", SwitchOutcome.NotAvailable, result.Outcome);
        Equal<byte?>("input-switch laptop before HDMI setup requested", null, result.Requested);
        Equal<uint?>("input-switch laptop before HDMI setup current", null, result.Current);
        Check("input-switch laptop before HDMI setup has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckNoWrites("input-switch laptop before HDMI setup", writer);
        CheckReadPlan("input-switch laptop before HDMI setup", reads, 0);

        reads = new ScriptedReads().Add(Reading(Dp, Codes(Dp, Hdmi2)));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch already selected outcome", SwitchOutcome.AlreadySelected, result.Outcome);
        Equal<byte?>("input-switch already selected requested", Dp, result.Requested);
        Equal<uint?>("input-switch already selected current", Dp, result.Current);
        CheckNoWrites("input-switch already selected", writer);
        CheckReadPlan("input-switch already selected", reads, 1);

        reads = new ScriptedReads()
            .Add(Reading(Hdmi2, Codes(Dp)))
            .Add(LostConnection());
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch display disappears after ack outcome", SwitchOutcome.SentUnconfirmed, result.Outcome);
        Equal<byte?>("input-switch display disappears after ack requested", Dp, result.Requested);
        Equal<uint?>("input-switch display disappears after ack current", null, result.Current);
        Check("input-switch display disappears after ack has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckSingleWrite("input-switch display disappears after ack", writer, Dp);
        CheckReadPlan("input-switch display disappears after ack", reads, 2);
        EqualSequence("input-switch display disappears after ack pauses", new[] { 150 }, pauses);

        reads = new ScriptedReads().Add(Reading(Hdmi2, Codes(Dp)));
        writer = new RecordingWriter(new VcpWrite(false, 5, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch native write failure outcome", SwitchOutcome.SendFailed, result.Outcome);
        Equal<byte?>("input-switch native write failure requested", Dp, result.Requested);
        Equal<uint?>("input-switch native write failure current", null, result.Current);
        Check("input-switch native write failure has message", !string.IsNullOrWhiteSpace(result.Message));
        CheckSingleWrite("input-switch native write failure", writer, Dp);
        CheckReadPlan("input-switch native write failure", reads, 1);
        EqualSequence("input-switch native write failure pauses", Array.Empty<int>(), pauses);

        reads = new ScriptedReads()
            .Add(Reading(Hdmi2, Codes(Dp)))
            .Add(Reading(0x123u, null))
            .Add(Reading(0x123u, null));
        writer = new RecordingWriter(new VcpWrite(true, null, null));
        pauses = new List<int>();

        result = InputSwitch.Execute(
            InputAction.Pc,
            dpToHdmiProfile,
            reads.Next,
            writer.Write,
            pauses.Add);

        Equal("input-switch full DWORD mismatch outcome", SwitchOutcome.SentUnconfirmed, result.Outcome);
        Equal<byte?>("input-switch full DWORD mismatch requested", Dp, result.Requested);
        Equal<uint?>("input-switch full DWORD mismatch current", 0x123u, result.Current);
        Check("input-switch full DWORD mismatch is not truncated", result.Current != 0x23u);
        CheckSingleWrite("input-switch full DWORD mismatch", writer, Dp);
        CheckReadPlan("input-switch full DWORD mismatch", reads, 3);
        EqualSequence("input-switch full DWORD mismatch pauses", new[] { 150, 350 }, pauses);
    }

    private static void RunCapabilityChecks()
    {
        var basic = VcpCapabilities.Parse("vcp(04 10 12 14(04 05) 60(11 12 0F) CC(00))");
        Check("capabilities basic advertises VCP 60", basic.AdvertisesVcp60);
        EqualSequence<byte>(
            "capabilities basic input codes",
            new byte[] { 0x11, 0x12, 0x0F },
            basic.Inputs);

        var nested = VcpCapabilities.Parse("vcp(14(60 0F) 60(11))");
        Check("capabilities nested value 60 does not hide top-level VCP 60", nested.AdvertisesVcp60);
        EqualSequence<byte>(
            "capabilities nested value 60 is not an input code",
            new byte[] { 0x11 },
            nested.Inputs);

        var onlyNested = VcpCapabilities.Parse("vcp(14(60 0F))");
        Check("capabilities nested 60 alone does not advertise VCP 60", !onlyNested.AdvertisesVcp60);
        EqualSequence<byte>("capabilities nested 60 alone has no inputs", null, onlyNested.Inputs);

        var unbalanced = VcpCapabilities.Parse("vcp(60(11 12)");
        Check("capabilities unbalanced parentheses are rejected", !unbalanced.AdvertisesVcp60);
        EqualSequence<byte>("capabilities unbalanced parentheses have no inputs", null, unbalanced.Inputs);

        var noBlock = VcpCapabilities.Parse("60 11 12");
        Check("capabilities without a vcp block are rejected", !noBlock.AdvertisesVcp60);

        var withoutList = VcpCapabilities.Parse("vcp(60)");
        Check("capabilities VCP 60 without input list advertises", withoutList.AdvertisesVcp60);
        EqualSequence<byte>("capabilities VCP 60 without input list has no codes", null, withoutList.Inputs);
    }

    private static void RunSettingsChecks()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MonitorInputSwitcher.Checks",
            Guid.NewGuid().ToString("N"));

        try
        {
            var defaultStore = new SettingsStore(Path.Combine(root, "default"));
            var (defaults, defaultError) = defaultStore.Load();
            Check(
                "settings missing file reports no error",
                defaultError is null,
                defaultError ?? string.Empty);
            Check("settings default HDMI port is unconfigured", defaults.HdmiPort is null);
            Check("settings default profile has no laptop code", defaults.ToProfile().Laptop is null);
            Equal("settings default PC code", Dp, defaults.PcCode);
            Check(
                "settings default does not allow unverified writes",
                !defaults.AllowUnverifiedWrites);

            var hdmi1 = new AppSettings { HdmiPort = 1 };
            Equal<byte?>("settings HDMI 1 maps to 0x11", Hdmi1, hdmi1.ToProfile().Laptop);
            Check("settings HDMI 1 profile is valid", hdmi1.Validate() is null);

            var hdmi2 = new AppSettings { HdmiPort = 2 };
            Equal<byte?>("settings HDMI 2 maps to 0x12", Hdmi2, hdmi2.ToProfile().Laptop);
            Check("settings HDMI 2 profile is valid", hdmi2.Validate() is null);

            var overrideTwo = new AppSettings { HdmiPort = 2, LaptopOverride = 0x05 };
            Equal<byte?>(
                "settings custom override wins over HDMI 2 default",
                (byte)0x05,
                overrideTwo.ToProfile().Laptop);
            Check("settings custom HDMI 2 override is valid", overrideTwo.Validate() is null);

            var overrideOne = new AppSettings { HdmiPort = 1, LaptopOverride = 0x10 };
            Equal<byte?>(
                "settings custom override wins over HDMI 1 default",
                (byte)0x10,
                overrideOne.ToProfile().Laptop);
            Check("settings custom HDMI 1 override is valid", overrideOne.Validate() is null);

            var duplicateCode = new AppSettings { HdmiPort = 1, PcCode = 0x11 };
            Check(
                "settings duplicate PC and laptop code is rejected",
                duplicateCode.Validate() is not null);

            var duplicateOverride = new AppSettings
            {
                HdmiPort = 2,
                PcCode = 0x05,
                LaptopOverride = 0x05
            };
            Check(
                "settings override equal to PC code is rejected",
                duplicateOverride.Validate() is not null);

            var duplicateChords = new AppSettings
            {
                HdmiPort = 1,
                PcHotkey = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x31),
                LaptopHotkey = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x31)
            };
            Check(
                "settings duplicate hotkey chords are rejected",
                duplicateChords.Validate() is not null);

            Check(
                "settings HDMI port 3 is rejected",
                new AppSettings { HdmiPort = 3 }.Validate() is not null);

            var invalidStore = new SettingsStore(Path.Combine(root, "invalid"));
            var saveRejected = false;
            try
            {
                invalidStore.Save(duplicateCode);
            }
            catch (ArgumentException)
            {
                saveRejected = true;
            }

            Check("settings Save rejects duplicate codes", saveRejected);
            Check(
                "settings Save does not create a file for invalid settings",
                !File.Exists(Path.Combine(invalidStore.DirectoryPath, "settings.json")));

            var roundtripDirectory = Path.Combine(root, "roundtrip");
            var roundtripStore = new SettingsStore(roundtripDirectory);
            var saved = new AppSettings
            {
                MonitorId = "MON-CHECK",
                HdmiPort = 2,
                PcCode = 0x0F,
                LaptopOverride = 0x13,
                AllowUnverifiedWrites = true,
                PcHotkey = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x31),
                LaptopHotkey = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x32),
                ToggleHotkey = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x33)
            };

            Check("settings roundtrip source is valid", saved.Validate() is null);
            roundtripStore.Save(saved);

            var (loaded, loadError) = roundtripStore.Load();
            Check("settings roundtrip reports no error", loadError is null, loadError ?? string.Empty);
            Equal("settings roundtrip monitor id", saved.MonitorId, loaded.MonitorId);
            Equal("settings roundtrip HDMI port", saved.HdmiPort, loaded.HdmiPort);
            Equal("settings roundtrip PC code", saved.PcCode, loaded.PcCode);
            Equal<byte?>("settings roundtrip laptop override", saved.LaptopOverride, loaded.LaptopOverride);
            Equal(
                "settings roundtrip unverified writes",
                saved.AllowUnverifiedWrites,
                loaded.AllowUnverifiedWrites);
            Equal("settings roundtrip PC hotkey", saved.PcHotkey, loaded.PcHotkey);
            Equal("settings roundtrip laptop hotkey", saved.LaptopHotkey, loaded.LaptopHotkey);
            Equal("settings roundtrip toggle hotkey", saved.ToggleHotkey, loaded.ToggleHotkey);
            Equal<byte?>(
                "settings roundtrip profile laptop",
                (byte)0x13,
                loaded.ToProfile().Laptop);
            Equal("settings store directory", roundtripDirectory, roundtripStore.DirectoryPath);
            Check(
                "settings Save writes settings.json",
                File.Exists(Path.Combine(roundtripDirectory, "settings.json")));

            var malformedDirectory = Path.Combine(root, "malformed");
            Directory.CreateDirectory(malformedDirectory);
            var malformedPath = Path.Combine(malformedDirectory, "settings.json");
            const string malformedJson = "{ \"HdmiPort\": 2, \"PcCode\": ";
            File.WriteAllText(malformedPath, malformedJson);

            var (malformedSettings, malformedError) =
                new SettingsStore(malformedDirectory).Load();
            Check(
                "settings malformed JSON reports an error",
                malformedError is not null);
            Check(
                "settings malformed JSON falls back to defaults",
                malformedSettings.HdmiPort is null &&
                malformedSettings.ToProfile().Laptop is null);
            Equal(
                "settings malformed JSON file is preserved",
                malformedJson,
                File.ReadAllText(malformedPath));

            var invalidJsonDirectory = Path.Combine(root, "invalid-json");
            Directory.CreateDirectory(invalidJsonDirectory);
            var invalidJsonPath = Path.Combine(invalidJsonDirectory, "settings.json");
            const string duplicateJson = "{ \"HdmiPort\": 1, \"PcCode\": 17 }";
            File.WriteAllText(invalidJsonPath, duplicateJson);

            var (invalidLoaded, invalidError) =
                new SettingsStore(invalidJsonDirectory).Load();
            Check(
                "settings stored duplicate code reports an error",
                invalidError is not null);
            Check(
                "settings stored duplicate code falls back to defaults",
                invalidLoaded.HdmiPort is null);
            Equal(
                "settings stored duplicate code file is preserved",
                duplicateJson,
                File.ReadAllText(invalidJsonPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RunHotkeyChecks()
    {
        var parsed = HotkeyChord.TryParse("ctrl+alt+1", out var basic);
        Check("hotkey parses ctrl+alt+1", parsed);
        Equal(
            "hotkey ctrl+alt+1 chord",
            new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x31),
            basic);
        Equal("hotkey ToString keeps canonical order", "Ctrl+Alt+1", basic.ToString());

        parsed = HotkeyChord.TryParse("alt+shift+f11", out var f11);
        Check("hotkey parses alt+shift+F11", parsed);
        Equal<uint>("hotkey F11 virtual key", 0x7Au, f11.VirtualKey);
        Equal("hotkey ToString normalizes modifier order", "Alt+Shift+F11", f11.ToString());

        parsed = HotkeyChord.TryParse("Shift+Alt+F13", out var f13);
        Check("hotkey accepts F13", parsed);
        Equal<uint>("hotkey F13 virtual key", 0x7Cu, f13.VirtualKey);
        Equal("hotkey F13 ToString", "Alt+Shift+F13", f13.ToString());

        Check("hotkey rejects F12 token", !HotkeyChord.TryParse("Ctrl+Alt+F12", out _));
        Check(
            "hotkey rejects F12 chord value",
            !new HotkeyChord(HotkeyChord.Ctrl, 0x7B).IsValid());
        Check("hotkey rejects missing modifier", !HotkeyChord.TryParse("F11", out _));
        Check("hotkey rejects duplicate modifier", !HotkeyChord.TryParse("Ctrl+Ctrl+1", out _));
        Check("hotkey rejects blank text", !HotkeyChord.TryParse("   ", out _));
    }

    private static void RunArgumentChecks()
    {
        var parsed = AppInstance.TryParseArgs(Array.Empty<string>(), out var command);
        Check("args empty array is accepted", parsed);
        Equal("args empty array means open", AppCommand.Open, command);

        parsed = AppInstance.TryParseArgs(new[] { "--tray" }, out command);
        Check("args --tray is accepted", parsed);
        Equal("args --tray means tray", AppCommand.Tray, command);

        parsed = AppInstance.TryParseArgs(new[] { "--PC" }, out command);
        Check("args --PC is case insensitive", parsed);
        Equal("args --PC means PC", AppCommand.Pc, command);

        parsed = AppInstance.TryParseArgs(new[] { "--laptop" }, out command);
        Check("args --laptop is accepted", parsed);
        Equal("args --laptop means laptop", AppCommand.Laptop, command);

        Check(
            "args unknown option is rejected",
            !AppInstance.TryParseArgs(new[] { "--bogus" }, out _));
        Check(
            "args multiple options are rejected",
            !AppInstance.TryParseArgs(new[] { "--pc", "--tray" }, out _));
        Check(
            "args empty option is rejected",
            !AppInstance.TryParseArgs(new[] { string.Empty }, out _));
        Check(
            "args null array is rejected",
            !AppInstance.TryParseArgs(null!, out _));
    }

    private static void RunPairingInviteChecks()
    {
        var digest = new byte[32];
        for (var index = 0; index < digest.Length; index++)
        {
            digest[index] = (byte)(index + 1);
        }

        var codeProperty = typeof(LanPairingInvite).GetProperty(nameof(LanPairingInvite.Code));
        Check("pairing code property exists", codeProperty is not null);
        Check(
            "pairing code property has no setter",
            codeProperty is not null && !codeProperty.CanWrite);

        var shortDigestRejected = false;
        try
        {
            _ = new LanPairingInvite(new byte[31]);
        }
        catch (ArgumentException)
        {
            shortDigestRejected = true;
        }

        Check("pairing constructor rejects a short digest", shortDigestRejected);

        var time = new FakeTimeProvider();
        var invite = new LanPairingInvite(digest, time);
        var code = invite.Code;

        Check("pairing code keeps the MIS1 prefix", code.StartsWith("MIS1-", StringComparison.Ordinal));
        Equal("pairing code length", 69, code.Length);
        Check(
            "pairing code uses only base64url characters without padding",
            code.IndexOf('+') < 0 && code.IndexOf('/') < 0 && code.IndexOf('=') < 0);
        Check("pairing code has no whitespace", code.IndexOf(' ') < 0 && code.IndexOf('\t') < 0);

        var parsed = LanPairingInvite.TryParse(code, out var parsedDigest, out var parsedSecret);
        Check("pairing code parses", parsed);
        EqualSequence("pairing parsed digest equals the supplied digest", digest, parsedDigest);
        Equal("pairing parsed secret length", 16, parsedSecret.Length);

        Check(
            "pairing code parses when pasted with surrounding whitespace",
            LanPairingInvite.TryParse(" \t" + code + "\r\n", out var pastedDigest, out var pastedSecret));
        EqualSequence("pairing pasted digest equals the supplied digest", digest, pastedDigest);
        Equal("pairing pasted secret length", 16, pastedSecret.Length);

        CheckRejected("pairing null text", null);
        CheckRejected("pairing empty text", string.Empty);
        CheckRejected("pairing whitespace text", "   ");
        CheckRejected("pairing unknown prefix", "MIS2-" + code.Substring(5));
        CheckRejected("pairing lowercase prefix", "mis1-" + code.Substring(5));
        CheckRejected("pairing missing prefix", code.Substring(5));
        CheckRejected("pairing truncated code", code.Substring(0, code.Length - 1));
        CheckRejected("pairing over long code", code + "A");
        CheckRejected("pairing trailing padding", code + "=");
        CheckRejected("pairing plus character", "MIS1-+" + code.Substring(6));
        CheckRejected("pairing slash character", "MIS1-/" + code.Substring(6));
        CheckRejected("pairing non ASCII character", "MIS1-\u00e9" + code.Substring(6));

        var payload = PairingPayload(parsedDigest, parsedSecret);
        Equal("pairing decoded payload length", 48, payload.Length);

        var persistCalls = 0;

        var tamperedSecret = (byte[])payload.Clone();
        tamperedSecret[47] ^= 0x01;
        Check(
            "pairing tampered secret is rejected",
            !invite.TryRedeem(tamperedSecret, () => persistCalls++));
        Equal("pairing tampered secret does not persist", 0, persistCalls);

        var tamperedDigest = (byte[])payload.Clone();
        tamperedDigest[0] ^= 0x80;
        Check(
            "pairing tampered digest is rejected",
            !invite.TryRedeem(tamperedDigest, () => persistCalls++));
        Equal("pairing tampered digest does not persist", 0, persistCalls);

        Check(
            "pairing short payload is rejected",
            !invite.TryRedeem(new byte[47], () => persistCalls++));
        Equal("pairing short payload does not persist", 0, persistCalls);

        var nullPersistThrew = false;
        try
        {
            invite.TryRedeem(payload, null!);
        }
        catch (ArgumentNullException)
        {
            nullPersistThrew = true;
        }

        Check("pairing redeem rejects a null persist action", nullPersistThrew);

        string? committedClient = null;
        var persistRan = false;
        var reentrantAccepted = true;
        var redeemed = invite.TryRedeem(
            payload,
            () =>
            {
                persistRan = true;
                reentrantAccepted = invite.TryRedeem(payload, () => committedClient = "nested");
                committedClient = "client-1";
            });

        Check("pairing redeem succeeds with the generated code", redeemed);
        Check("pairing redeem runs persistence", persistRan);
        Check("pairing redeem is not re-entrant while persisting", !reentrantAccepted);
        Equal("pairing redeem commits the validated client identity", "client-1", committedClient);

        var replayPersisted = false;
        Check(
            "pairing replay after consumption fails",
            !invite.TryRedeem(payload, () => replayPersisted = true));
        Check("pairing replay does not persist", !replayPersisted);
        Equal("pairing replay keeps the committed identity", "client-1", committedClient);

        var retryTime = new FakeTimeProvider();
        var retryInvite = new LanPairingInvite(digest, retryTime);
        var retryParsed = LanPairingInvite.TryParse(
            retryInvite.Code,
            out var retryDigest,
            out var retrySecret);
        Check("pairing retry fixture parses", retryParsed);
        EqualSequence("pairing retry digest equals the supplied digest", digest, retryDigest);
        var retryPayload = PairingPayload(retryDigest, retrySecret);

        var failedPersistCalls = 0;
        var persistFailurePropagated = false;
        try
        {
            retryInvite.TryRedeem(
                retryPayload,
                () =>
                {
                    failedPersistCalls++;
                    throw new IOException("pairing persistence unavailable");
                });
        }
        catch (IOException)
        {
            persistFailurePropagated = true;
        }

        Equal("pairing failed persistence calls persist once", 1, failedPersistCalls);
        Check("pairing failed persistence propagates the exception", persistFailurePropagated);

        string? retryClient = null;
        var retryRedeemed = retryInvite.TryRedeem(retryPayload, () => retryClient = "client-2");
        Check("pairing redeem succeeds after failed persistence", retryRedeemed);
        Equal("pairing retry commits the client identity", "client-2", retryClient);

        var retryReplayPersisted = false;
        Check(
            "pairing retry replay fails",
            !retryInvite.TryRedeem(retryPayload, () => retryReplayPersisted = true));
        Check("pairing retry replay does not persist", !retryReplayPersisted);

        var boundaryTime = new FakeTimeProvider();
        var freshInvite = new LanPairingInvite(digest, boundaryTime);
        var freshParsed = LanPairingInvite.TryParse(
            freshInvite.Code,
            out var freshDigest,
            out var freshSecret);
        Check("pairing boundary fixture parses", freshParsed);
        EqualSequence("pairing boundary digest equals the supplied digest", digest, freshDigest);
        var freshPayload = PairingPayload(freshDigest, freshSecret);

        boundaryTime.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var justBeforeExpiryPersisted = false;
        Check(
            "pairing redeem one tick before five minutes succeeds",
            freshInvite.TryRedeem(freshPayload, () => justBeforeExpiryPersisted = true));
        Check("pairing redeem one tick before five minutes persists", justBeforeExpiryPersisted);

        var expiryTime = new FakeTimeProvider();
        var expiringInvite = new LanPairingInvite(digest, expiryTime);
        var expiringParsed = LanPairingInvite.TryParse(
            expiringInvite.Code,
            out var expiringDigest,
            out var expiringSecret);
        Check("pairing expiry fixture parses", expiringParsed);
        EqualSequence("pairing expiry digest equals the supplied digest", digest, expiringDigest);
        var expiringPayload = PairingPayload(expiringDigest, expiringSecret);

        expiryTime.Advance(TimeSpan.FromMinutes(5));
        var expiredPersisted = false;
        Check(
            "pairing redeem at exactly five minutes fails",
            !expiringInvite.TryRedeem(expiringPayload, () => expiredPersisted = true));
        Check("pairing expired invitation does not persist", !expiredPersisted);

        expiryTime.Advance(TimeSpan.FromMinutes(1));
        Check(
            "pairing expired invitation stays rejected",
            !expiringInvite.TryRedeem(expiringPayload, () => expiredPersisted = true));
        Check("pairing expired invitation still does not persist", !expiredPersisted);

        var invalidateTime = new FakeTimeProvider();
        var invalidatedInvite = new LanPairingInvite(digest, invalidateTime);
        var invalidatedParsed = LanPairingInvite.TryParse(
            invalidatedInvite.Code,
            out var invalidateDigest,
            out var invalidateSecret);
        Check("pairing invalidate fixture parses", invalidatedParsed);
        EqualSequence("pairing invalidate digest equals the supplied digest", digest, invalidateDigest);
        var invalidatePayload = PairingPayload(invalidateDigest, invalidateSecret);

        invalidatedInvite.Invalidate();
        var invalidatedPersisted = false;
        Check(
            "pairing redeem after invalidation fails",
            !invalidatedInvite.TryRedeem(invalidatePayload, () => invalidatedPersisted = true));
        Check("pairing invalidated invitation does not persist", !invalidatedPersisted);

        var secondInvalidateThrew = false;
        try
        {
            invalidatedInvite.Invalidate();
        }
        catch (Exception)
        {
            secondInvalidateThrew = true;
        }

        Check("pairing repeated invalidation is safe", !secondInvalidateThrew);

        invalidateTime.Advance(TimeSpan.FromMinutes(5));
        Check(
            "pairing invalidated invitation stays rejected after expiry",
            !invalidatedInvite.TryRedeem(invalidatePayload, () => invalidatedPersisted = true));

        var otherInvite = new LanPairingInvite(digest, new FakeTimeProvider());
        Check(
            "pairing generated codes are not repeated",
            !string.Equals(code, otherInvite.Code, StringComparison.Ordinal));

        void CheckRejected(string name, string? text)
        {
            var accepted = LanPairingInvite.TryParse(
                text,
                out var rejectedDigest,
                out var rejectedSecret);
            Check($"{name} is rejected", !accepted);
            Equal($"{name} leaves no digest", 0, rejectedDigest.Length);
            Equal($"{name} leaves no secret", 0, rejectedSecret.Length);
        }
    }

    private static byte[] PairingPayload(byte[] certificateSha256, byte[] secret)
    {
        var payload = new byte[certificateSha256.Length + secret.Length];
        certificateSha256.CopyTo(payload, 0);
        secret.CopyTo(payload, certificateSha256.Length);
        return payload;
    }

    private static int RunProbe()
    {
        try
        {
            var discovery = new DisplayDiscovery();
            var targets = discovery.List();

            Console.WriteLine($"Active display targets: {targets.Count}");
            Console.WriteLine($"G255F matches: {targets.Count(target => target.IsG255F)}");

            foreach (var target in targets)
            {
                Console.WriteLine(
                    $"- name='{target.Name}' id='{target.Id}' " +
                    $"g255f={target.IsG255F} unambiguous={target.IsUnambiguous}");
            }

            var monitor = new DdcMonitor(discovery);
            foreach (var target in targets.Where(target => target.IsG255F && target.IsUnambiguous))
            {
                var read = monitor.Read(target.Id, includeCapabilities: true);

                Console.WriteLine($"Probe '{target.Name}' ({target.Id})");
                Console.WriteLine($"  observed input: {FormatCode(read.Current)}");
                Console.WriteLine($"  advertises VCP 60: {read.AdvertisesVcp60}");
                Console.WriteLine($"  advertised input codes: {FormatCodes(read.AdvertisedInputs)}");

                if (read.ConnectionError is not null)
                {
                    Console.WriteLine($"  connection error: {read.ConnectionError}");
                }

                if (read.ReadError is int readError)
                {
                    Console.WriteLine($"  read error: Win32 {readError}");
                }

                if (read.CapabilitiesError is int capabilitiesError)
                {
                    Console.WriteLine($"  capabilities error: Win32 {capabilitiesError}");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed: {ex}");
            return 1;
        }
    }

    private static VcpRead Reading(uint? current, IReadOnlyList<byte>? advertisedInputs) =>
        new(current, null, advertisedInputs, false, null, null, null);

    private static VcpRead LostConnection() =>
        new(null, null, null, false, null, null, "display connection lost");

    private static byte[] Codes(params byte[] values) => values;

    private static void CheckReadPlan(string name, ScriptedReads reads, int expectedCount)
    {
        Equal($"{name} read count", expectedCount, reads.Count);

        if (expectedCount > 0)
        {
            Check($"{name} first read includes capabilities", reads.IncludeCapabilities[0]);
        }

        if (expectedCount > 1)
        {
            Check($"{name} follow-up reads exclude capabilities", reads.FollowUpsAreReadOnly);
        }
    }

    private static void CheckSingleWrite(string name, RecordingWriter writer, byte expected)
    {
        Equal($"{name} write count", 1, writer.Values.Count);
        if (writer.Values.Count == 1)
        {
            Equal($"{name} written code", expected, writer.Values[0]);
        }
    }

    private static void CheckNoWrites(string name, RecordingWriter writer) =>
        Equal($"{name} write count", 0, writer.Values.Count);

    private static void Equal<T>(string name, T expected, T actual)
    {
        _checks++;
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine($"FAIL {name}: expected <{expected}>, actual <{actual}>");
    }

    private static void EqualSequence<T>(
        string name,
        IReadOnlyList<T>? expected,
        IReadOnlyList<T>? actual)
    {
        _checks++;

        var matches = expected is null
            ? actual is null
            : actual is not null && expected.SequenceEqual(actual);

        if (matches)
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine(
            $"FAIL {name}: expected <{FormatSequence(expected)}>, " +
            $"actual <{FormatSequence(actual)}>");
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition)
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine(
            detail is null ? $"FAIL {name}" : $"FAIL {name}: {detail}");
    }

    private static void Fail(string name, string detail)
    {
        _checks++;
        _failures++;
        Console.Error.WriteLine($"FAIL {name}: {detail}");
    }

    private static string FormatCode(uint? value) =>
        value is uint code ? $"0x{code:X2}" : "unknown";

    private static string FormatCodes(IReadOnlyList<byte>? codes) =>
        codes is null
            ? "unknown"
            : codes.Count == 0
                ? "none"
                : string.Join(", ", codes.Select(code => $"0x{code:X2}"));

    private static string FormatSequence<T>(IReadOnlyList<T>? values) =>
        values is null
            ? "<null>"
            : $"[{string.Join(", ", values)}]";

    private sealed class ScriptedReads
    {
        private readonly Queue<VcpRead> _pending = new();

        public List<bool> IncludeCapabilities { get; } = new();

        public int Count => IncludeCapabilities.Count;

        public bool FollowUpsAreReadOnly =>
            IncludeCapabilities.Skip(1).All(includeCapabilities => !includeCapabilities);

        public ScriptedReads Add(VcpRead read)
        {
            _pending.Enqueue(read);
            return this;
        }

        public VcpRead Next(bool includeCapabilities)
        {
            IncludeCapabilities.Add(includeCapabilities);

            if (_pending.Count == 0)
            {
                throw new InvalidOperationException(
                    "InputSwitch performed an unexpected extra read.");
            }

            return _pending.Dequeue();
        }
    }

    private sealed class RecordingWriter
    {
        private readonly VcpWrite _result;

        public RecordingWriter(VcpWrite result)
        {
            _result = result;
        }

        public List<byte> Values { get; } = new();

        public VcpWrite Write(byte value)
        {
            Values.Add(value);
            return _result;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _elapsedTicks;

        public override long GetTimestamp() => _elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => _elapsedTicks += delta.Ticks;
    }
}
