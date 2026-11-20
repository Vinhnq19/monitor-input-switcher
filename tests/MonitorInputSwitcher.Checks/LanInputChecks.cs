using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;

namespace MonitorInputSwitcher.Checks;

internal static class LanInputChecks
{
    public static async Task RunAsync(LanTestAssertion check)
    {
        var source = new Rectangle(-2560, -200, 2560, 1440);
        var target = new Rectangle(0, 100, 1920, 1080);
        foreach (var edge in Enum.GetValues<LanEdge>())
        {
            int expected = LanGeometry.Unit / 3;
            var exit = LanGeometry.AtEdge(source, edge, expected, 0);
            int ratio = LanGeometry.Ratio(source, exit, edge);
            var arrivalEdge = LanGeometry.Opposite(edge);
            var arrival = LanGeometry.AtEdge(target, arrivalEdge, ratio);
            check($"geometry {edge} maps into the target in mixed resolutions", target.Contains(arrival));
            check($"geometry {edge} maps the orthogonal coordinate", Math.Abs(LanGeometry.Ratio(target, arrival, arrivalEdge) - expected) < 90);
            check($"geometry {edge} enters one pixel inside", LanGeometry.Distance(target, arrival, arrivalEdge) == 1);
        }
        var right = LanGeometry.AtEdge(source, LanEdge.Right, LanGeometry.Unit / 2, 0);
        check("geometry adjacent local screen wins", !LanGeometry.CanCross(source, [source, target], right, LanEdge.Right, false));
        check("geometry exposed edge available", LanGeometry.CanCross(source, [source], right, LanEdge.Right, true));
        var corner = new Point(source.Right - 1, source.Top);
        check("geometry corner guard applies only when enabled",
            !LanGeometry.CanCross(source, [source], corner, LanEdge.Right, true) &&
            LanGeometry.CanCross(source, [source], corner, LanEdge.Right, false));
        check("custom hotkey can roundtrip an arrow",
            HotkeyChord.TryParse("Ctrl+Alt+Shift+Right", out var chord) && chord.VirtualKey == 0x27 &&
            chord.ToString() == "Ctrl+Alt+Shift+Right");
        check("custom hotkey can roundtrip emergency Escape",
            HotkeyChord.TryParse("Ctrl+Alt+Shift+Esc", out chord) && chord.ToString() == "Ctrl+Alt+Shift+Esc");

        var route = new LanPrivateRoute(IPAddress.Parse("192.168.72.3"), IPAddress.Parse("255.255.255.0"));
        check("Private subnet includes only ordinary peers",
            route.ContainsPeer(IPAddress.Parse("192.168.72.1")) &&
            !route.ContainsPeer(IPAddress.Parse("192.168.72.0")) &&
            !route.ContainsPeer(IPAddress.Parse("192.168.72.255")) &&
            !route.ContainsPeer(IPAddress.Parse("192.168.73.1")) &&
            !route.ContainsPeer(route.LocalAddress));
        var builtIn = new LanScreen("Built-in", target, true, true, false);
        var shared = new LanScreen("MSI", source, false, false, true);
        check("automatic handoff prefers the built-in laptop panel and the shared PC display",
            LanScreens.Pick([shared, builtIn], null, LanRole.Laptop) == builtIn &&
            LanScreens.Pick([builtIn, shared], null, LanRole.Pc) == shared);
        check("an explicitly chosen detached screen disables its portal",
            LanScreens.Pick([builtIn], shared.Name, LanRole.Laptop) is null);
        check("Win32 Raw Input mouse payload layout", Marshal.SizeOf<WindowsLanInput.RawMouse>() == 24);
        check("Win32 low-level hook layouts",
            Marshal.SizeOf<WindowsLanInput.KeyHook>() == (IntPtr.Size == 8 ? 24 : 20) &&
            Marshal.SizeOf<WindowsLanInput.MouseHook>() == (IntPtr.Size == 8 ? 32 : 24));
        CheckOptions(check);

        // Synthetic timestamps and scan codes, with interleaved key transitions at 150 WPM
        // and a 1000 Hz mouse. The wire is real; Windows IME and hardware input are separate checks.
        var expectedEvents = new List<LanInputEvent>();
        LanInputEvent[] keystrokes =
        [
            new(LanInputKind.Key, 0x1D, 0, 0), new(LanInputKind.Key, 0x2E, 0, 0),
            new(LanInputKind.Key, 0x2E, 2, 0), new(LanInputKind.Key, 0x1D, 2, 0),
            new(LanInputKind.Key, 0x2A, 0, 0), new(LanInputKind.Key, 0x11, 0, 0),
            new(LanInputKind.Key, 0x11, 0, 0), new(LanInputKind.Key, 0x11, 2, 0),
            new(LanInputKind.Key, 0x2A, 2, 0), new(LanInputKind.Key, 0x4B, 1, 0),
            new(LanInputKind.Key, 0x4B, 3, 0)
        ];
        for (int ms = 0; ms < 12_000; ms++)
        {
            if (ms % 40 == 0)
            {
                expectedEvents.Add(keystrokes[ms / 40 % keystrokes.Length] with { Timestamp = ms + 1 });
            }
            if (ms % 100 == 0)
            {
                expectedEvents.Add(new LanInputEvent(LanInputKind.Button, 0, ms / 100 % 2, ms + 1));
            }
            expectedEvents.Add(new LanInputEvent(LanInputKind.Move, 1, -2, ms + 1));
            if (ms % 200 == 0)
            {
                expectedEvents.Add(new LanInputEvent(LanInputKind.Wheel, -120, ms / 200 % 2, ms + 1));
            }
        }

        using var stream = new MemoryStream();
        var writer = new LanWire(stream);
        foreach (var input in expectedEvents)
        {
            await writer.WriteAsync(LanFrameType.Input, input.Encode(), CancellationToken.None);
        }
        stream.Position = 0;
        var reader = new LanWire(stream);
        bool ordered = true;
        for (int index = 0; index < expectedEvents.Count; index++)
        {
            var frame = await reader.ReadAsync(CancellationToken.None);
            if (frame is not { Type: LanFrameType.Input } good || good.Sequence != (ulong)index + 1 ||
                LanInputEvent.Decode(good.Payload) != expectedEvents[index])
            {
                ordered = false;
                break;
            }
        }
        check("simulated 1000 Hz mouse and 150 WPM keyboard keep all key, button and wheel ordering",
            ordered && await reader.ReadAsync(CancellationToken.None) is null);
        Reject(check, new byte[1], "truncated input");
        Reject(check, new LanInputEvent((LanInputKind)255, 0, 0, 0).Encode(), "unknown input kind");
        Reject(check, new LanInputEvent(LanInputKind.Key, 0, 0, 0).Encode(), "zero scan code");
        Reject(check, new LanInputEvent(LanInputKind.Key, 0x41, 4, 0).Encode(), "arbitrary virtual key");
        Reject(check, new LanInputEvent(LanInputKind.Button, 5, 0, 0).Encode(), "invalid button");
        Reject(check, new LanInputEvent(LanInputKind.Move, int.MaxValue, 0, 0).Encode(), "invalid delta");
    }

    private static void CheckOptions(LanTestAssertion check)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"mis-lan-options-{Guid.NewGuid():N}");
        try
        {
            check("LAN sharing defaults to disabled", !LanSharingOptions.Load(folder).Enabled);
            var settings = new LanSharingOptions { Enabled = true, ManualAddress = "192.168.72.1", Threshold = 9 };
            check("sharing customization preserves local recovery shortcuts",
                !(settings with { ToPc = new HotkeyChord(HotkeyChord.Ctrl | HotkeyChord.Shift, 0x1B) }).Valid() &&
                !(settings with { ToPc = new HotkeyChord(HotkeyChord.Win, 0x4C) }).Valid() &&
                !(settings with { ToPc = new HotkeyChord(HotkeyChord.Alt, 0x73) }).Valid());
            settings.Save(folder);
            var loaded = LanSharingOptions.Load(folder);
            check("LAN options roundtrip independently and preserve IPv4 and shortcut settings",
                loaded == settings && loaded.Address?.ToString() == settings.ManualAddress && loaded.Valid());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                File.Delete(Path.Combine(folder, "lan-options.json"));
                Directory.Delete(folder);
            }
        }
    }

    private static void Reject(LanTestAssertion check, byte[] bytes, string scenario)
    {
        bool rejected = false;
        try
        {
            _ = LanInputEvent.Decode(bytes);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        check($"input rejects {scenario}", rejected);
    }
}
