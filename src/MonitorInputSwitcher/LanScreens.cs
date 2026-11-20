using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MonitorInputSwitcher;

internal sealed record LanScreen(string Name, Rectangle Bounds, bool Primary, bool BuiltIn, bool Shared)
{
    public override string ToString() => $"{Name} · {Bounds.Width}×{Bounds.Height}" +
        (BuiltIn ? UiText.Text(" · Tích hợp", " · Built-in") : Shared ? " · MSI G255F" : "");
}

internal static class LanScreens
{
    public static LanScreen[] Read()
    {
        var internalNames = InternalScreens();
        IReadOnlySet<string> sharedNames;
        try
        {
            sharedNames = DisplayDiscovery.SharedScreenNames();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            sharedNames = new HashSet<string>();
        }
        return Forms.Screen.AllScreens.Select(screen => new LanScreen(screen.DeviceName, screen.Bounds,
            screen.Primary, internalNames.Contains(screen.DeviceName), sharedNames.Contains(screen.DeviceName))).ToArray();
    }

    public static LanScreen? Pick(LanScreen[] screens, string? name, LanRole role)
    {
        if (name is not null)
        {
            return screens.FirstOrDefault(screen => screen.Name == name);
        }
        return screens.FirstOrDefault(screen => role == LanRole.Pc ? screen.Shared : screen.BuiltIn) ??
            screens.FirstOrDefault(screen => screen.Primary) ?? screens.FirstOrDefault();
    }

    private static HashSet<string> InternalScreens()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0 ||
                pathCount > 256 || modeCount > 512 || pathCount == 0)
            {
                break;
            }
            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            int result = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == 122)
            {
                continue;
            }
            if (result != 0)
            {
                break;
            }
            foreach (var path in paths.Take((int)pathCount))
            {
                if (path.Target.Technology is not (6 or 11 or 13 or 0x80000000))
                {
                    continue;
                }
                var source = new SourceName
                {
                    Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter,
                    Id = path.Source.Id, Name = string.Empty
                };
                if (DisplayConfigGetDeviceInfo(ref source) == 0 && source.Name.Length > 0)
                {
                    names.Add(source.Name);
                }
            }
            break;
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SourcePath { public Luid Adapter; public uint Id, Mode, Status; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetPath
    {
        public Luid Adapter;
        public uint Id, Mode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLine, Available, Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath { public SourcePath Source; public TargetPath Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayMode { [FieldOffset(0)] public ulong Alignment; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
}
