using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorInputSwitcher;

public sealed record DisplayTarget(string Id, string Name, bool IsG255F, bool IsUnambiguous);

public sealed class DisplayDiscovery
{
    internal static IReadOnlySet<string> SharedScreenNames() => EnumerateMappings()
        .Where(mapping => mapping.ActiveInterfaces.Any(IsG255F))
        .Select(mapping => mapping.GdiName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private const uint EddGetDeviceInterfaceName = 0x00000001;
    private const uint DisplayDeviceActive = 0x00000001;
    private const string MonitorInterfaceClassGuid = "{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string G255FHardwareSegment = "MSI3BC0";
    private const string G255FFriendlyName = "MSI G255F";
    private const string G255FShortName = "G255F";

    public IReadOnlyList<DisplayTarget> List()
    {
        var groups = new Dictionary<string, TargetGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in EnumerateMappings())
        {
            foreach (var physical in mapping.ActiveInterfaces)
            {
                if (!physical.IsComplete)
                {
                    continue;
                }

                if (!groups.TryGetValue(physical.Id, out var group))
                {
                    group = new TargetGroup(physical.Id);
                    groups.Add(physical.Id, group);
                }

                group.Add(mapping, physical);
            }
        }

        var targets = new List<DisplayTarget>(groups.Count);
        foreach (var group in groups.Values)
        {
            targets.Add(group.ToTarget());
        }

        return targets;
    }

    internal nint Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        GdiMonitorMapping? match = null;
        var matchingOccurrences = 0;
        var matchingMappings = 0;

        foreach (var mapping in EnumerateMappings())
        {
            var occurrences = mapping.ActiveInterfaces.Count(physical =>
                physical.IsComplete &&
                string.Equals(physical.Id, id, StringComparison.OrdinalIgnoreCase));

            if (occurrences == 0)
            {
                continue;
            }

            matchingOccurrences += occurrences;
            matchingMappings++;
            match = mapping;
        }

        if (matchingOccurrences != 1 || matchingMappings != 1 || match is null || !match.IsUnambiguous)
        {
            return 0;
        }

        return match.Handle;
    }

    private static List<GdiMonitorMapping> EnumerateMappings()
    {
        var handles = new List<nint>();
        MonitorEnumProc callback = (nint hMonitor, nint hdc, ref Rect lprc, nint data) =>
        {
            handles.Add(hMonitor);
            return true;
        };

        try
        {
            if (!EnumDisplayMonitors(0, 0, callback, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            GC.KeepAlive(callback);
        }

        var mappings = new List<GdiMonitorMapping>(handles.Count);
        foreach (var handle in handles)
        {
            mappings.Add(CreateMapping(handle));
        }

        return mappings;
    }

    private static GdiMonitorMapping CreateMapping(nint handle)
    {
        var monitorInfo = new MonitorInfoExW
        {
            cbSize = (uint)Marshal.SizeOf<MonitorInfoExW>(),
            szDevice = string.Empty
        };

        if (!GetMonitorInfo(handle, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var gdiName = monitorInfo.szDevice ?? string.Empty;
        var activeInterfaces = new List<PhysicalInterface>();
        var hasIncompleteIdentity = false;

        for (uint index = 0; ; index++)
        {
            var device = new DisplayDeviceW
            {
                cb = (uint)Marshal.SizeOf<DisplayDeviceW>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };

            if (!EnumDisplayDevices(gdiName, index, ref device, EddGetDeviceInterfaceName))
            {
                break;
            }

            if ((device.StateFlags & DisplayDeviceActive) == 0)
            {
                continue;
            }

            var id = device.DeviceID?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                hasIncompleteIdentity = true;
                continue;
            }

            var isComplete = IsCompleteMonitorInterfaceId(id);
            activeInterfaces.Add(new PhysicalInterface(
                id,
                device.DeviceString?.Trim() ?? string.Empty,
                isComplete));

            if (!isComplete)
            {
                hasIncompleteIdentity = true;
            }
        }

        var distinctCompleteIds = activeInterfaces
            .Where(physical => physical.IsComplete)
            .Select(physical => physical.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var isUnambiguous = !hasIncompleteIdentity && distinctCompleteIds.Length == 1;

        return new GdiMonitorMapping(handle, gdiName, activeInterfaces, isUnambiguous);
    }

    private static bool IsCompleteMonitorInterfaceId(string id)
    {
        var value = id.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var guidSeparator = value.LastIndexOf('#');
        if (guidSeparator <= 0 || guidSeparator == value.Length - 1)
        {
            return false;
        }

        if (!string.Equals(
                value[(guidSeparator + 1)..],
                MonitorInterfaceClassGuid,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetHardwareSegment(value, out _);
    }

    private static bool TryGetHardwareSegment(string id, out string segment)
    {
        const string displayPrefix = @"\\?\DISPLAY#";

        segment = string.Empty;
        if (!id.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = displayPrefix.Length;
        var end = id.IndexOf('#', start);
        if (end <= start)
        {
            return false;
        }

        segment = id[start..end];
        return segment.Length > 0;
    }

    private static bool IsG255F(PhysicalInterface physical)
    {
        if (TryGetHardwareSegment(physical.Id, out var hardwareSegment) &&
            string.Equals(hardwareSegment.Trim(), G255FHardwareSegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = physical.Name.Trim();
        return string.Equals(name, G255FFriendlyName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, G255FShortName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TargetGroup
    {
        private readonly string _id;
        private readonly List<Occurrence> _occurrences = new();
        private bool _isG255F;

        public TargetGroup(string id)
        {
            _id = id;
        }

        public void Add(GdiMonitorMapping mapping, PhysicalInterface physical)
        {
            _occurrences.Add(new Occurrence(mapping, physical));
            _isG255F |= IsG255F(physical);
        }

        public DisplayTarget ToTarget()
        {
            var mappingCount = _occurrences
                .Select(occurrence => occurrence.Mapping.Handle)
                .Distinct()
                .Count();

            var isUnambiguous =
                _occurrences.Count == 1 &&
                mappingCount == 1 &&
                _occurrences[0].Mapping.IsUnambiguous;

            var name = _isG255F
                ? G255FFriendlyName
                : _occurrences
                    .Select(occurrence => occurrence.Physical.Name)
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
                  ?? _occurrences
                      .Select(occurrence => occurrence.Mapping.GdiName)
                      .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
                  ?? "Monitor";

            return new DisplayTarget(_id, name, _isG255F, isUnambiguous);
        }

        private sealed record Occurrence(GdiMonitorMapping Mapping, PhysicalInterface Physical);
    }

    private sealed record GdiMonitorMapping(
        nint Handle,
        string GdiName,
        IReadOnlyList<PhysicalInterface> ActiveInterfaces,
        bool IsUnambiguous);

    private sealed record PhysicalInterface(string Id, string Name, bool IsComplete);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref Rect lprc, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoExW
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public uint cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "EnumDisplayMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint lprcClip,
        MonitorEnumProc lpfnEnum,
        nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoExW lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDeviceW lpDisplayDevice,
        uint dwFlags);
}
