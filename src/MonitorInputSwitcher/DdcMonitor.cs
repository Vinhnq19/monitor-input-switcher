using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MonitorInputSwitcher;

public sealed record VcpRead(
    uint? Current,
    string? Capabilities,
    IReadOnlyList<byte>? AdvertisedInputs,
    bool AdvertisesVcp60,
    int? ReadError,
    int? CapabilitiesError,
    string? ConnectionError);

public sealed partial class DdcMonitor(DisplayDiscovery discovery)
{
    private const byte InputSourceVcpCode = 0x60;
    private const int MaxCapabilitiesCharacters = 16 * 1024;
    private const int ErrorInvalidData = 13;
    private const int ErrorBufferOverflow = 111;

    private readonly DisplayDiscovery _discovery = discovery;

    public VcpRead Read(string id, bool includeCapabilities)
    {
        using var monitor = Open(id, out var connectionProblem);
        if (monitor.IsInvalid)
        {
            return new VcpRead(
                null,
                null,
                null,
                false,
                null,
                null,
                connectionProblem ?? UiText.Text("Không thể kết nối tới màn hình đã chọn.", "Could not connect to the selected monitor."));
        }

        uint? current = null;
        int? readError = null;

        var readSucceeded = GetVCPFeatureAndVCPFeatureReply(
            monitor,
            InputSourceVcpCode,
            0,
            out var currentValue,
            0);

        if (readSucceeded)
        {
            current = currentValue;
        }
        else
        {
            readError = Marshal.GetLastWin32Error();
        }

        string? capabilities = null;
        IReadOnlyList<byte>? advertisedInputs = null;
        var advertisesVcp60 = false;
        int? capabilitiesError = null;

        if (includeCapabilities)
        {
            capabilities = TryReadCapabilities(monitor, out capabilitiesError);
            if (capabilities is not null)
            {
                var parsed = VcpCapabilities.Parse(capabilities);
                advertisesVcp60 = parsed.AdvertisesVcp60;
                advertisedInputs = parsed.Inputs;
            }
        }

        return new VcpRead(
            current,
            capabilities,
            advertisedInputs,
            advertisesVcp60,
            readError,
            capabilitiesError,
            null);
    }

    internal SafeHandle Open(string id, out string? problem)
    {
        problem = null;

        var monitorHandle = _discovery.Resolve(id);
        if (monitorHandle == 0)
        {
            problem = UiText.Text("Không thể kết nối tới màn hình đã chọn: không tìm thấy màn hình vật lý tương ứng. Hãy làm mới danh sách màn hình.", "The selected physical monitor was not found. Refresh the monitor list.");
            return new PhysicalMonitorHandle(0);
        }

        var countSucceeded = GetNumberOfPhysicalMonitorsFromHMONITOR(monitorHandle, out var count);
        if (!countSucceeded)
        {
            var error = Marshal.GetLastWin32Error();
            problem = (UiText.IsEnglish ? $"Could not query the number of physical monitors (Win32 error {error})." : $"Không thể kết nối tới màn hình đã chọn: không truy vấn được số lượng màn hình vật lý (mã lỗi Win32 {error}).");
            return new PhysicalMonitorHandle(0);
        }

        if (count != 1)
        {
            problem = count == 0
                ? UiText.Text("Không thể kết nối tới màn hình đã chọn: không có màn hình vật lý nào.", "No physical monitor is available for the selected display.")
                : (UiText.IsEnglish ? $"The selected display must map to exactly one physical monitor. Found {count}." : $"Không thể kết nối tới màn hình đã chọn: yêu cầu đúng một màn hình vật lý nhưng nhận được {count}.");
            return new PhysicalMonitorHandle(0);
        }

        var monitors = new PhysicalMonitor[1];
        monitors[0].szPhysicalMonitorDescription = string.Empty;

        bool monitorsSucceeded;
        try
        {
            monitorsSucceeded = GetPhysicalMonitorsFromHMONITOR(monitorHandle, 1, monitors);
        }
        catch
        {
            DestroyIfValid(monitors[0].hPhysicalMonitor);
            throw;
        }

        if (!monitorsSucceeded)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyIfValid(monitors[0].hPhysicalMonitor);
            problem = (UiText.IsEnglish ? $"Could not open the physical monitor (Win32 error {error})." : $"Không thể kết nối tới màn hình đã chọn: không mở được màn hình vật lý (mã lỗi Win32 {error}).");
            return new PhysicalMonitorHandle(0);
        }

        var physicalHandle = monitors[0].hPhysicalMonitor;
        if (physicalHandle == 0 || physicalHandle == -1)
        {
            problem = UiText.Text("Không thể kết nối tới màn hình đã chọn: handle màn hình vật lý không hợp lệ.", "The selected physical monitor handle is invalid.");
            return new PhysicalMonitorHandle(0);
        }

        try
        {
            return new PhysicalMonitorHandle(physicalHandle);
        }
        catch
        {
            DestroyIfValid(physicalHandle);
            throw;
        }
    }

    private static string? TryReadCapabilities(SafeHandle monitor, out int? error)
    {
        error = null;

        var lengthSucceeded = GetCapabilitiesStringLength(monitor, out var length);
        if (!lengthSucceeded)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        if (length == 0 || length > MaxCapabilitiesCharacters)
        {
            error = length == 0 ? ErrorInvalidData : ErrorBufferOverflow;
            return null;
        }

        var buffer = new byte[length];
        var replySucceeded = CapabilitiesRequestAndCapabilitiesReply(monitor, buffer, length);
        if (!replySucceeded)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        var terminator = Array.IndexOf(buffer, (byte)0);
        var textLength = terminator >= 0 ? terminator : buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, textLength);
    }

    private static void DestroyIfValid(nint handle)
    {
        if (handle != 0 && handle != -1)
        {
            DestroyPhysicalMonitor(handle);
        }
    }

    private sealed class PhysicalMonitorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PhysicalMonitorHandle(nint handle) : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return DestroyPhysicalMonitor(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint count);

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "GetPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        nint hMonitor,
        uint monitorArraySize,
        [Out] PhysicalMonitor[] monitorArray);

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "DestroyPhysicalMonitor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(nint hMonitor);

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "GetVCPFeatureAndVCPFeatureReply")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        SafeHandle hMonitor,
        byte vcpCode,
        nint vcpCodeType,
        out uint currentValue,
        nint maximumValue);

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "GetCapabilitiesStringLength")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(SafeHandle hMonitor, out uint length);

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "CapabilitiesRequestAndCapabilitiesReply")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(
        SafeHandle hMonitor,
        [Out] byte[] capabilities,
        uint length);

    public VcpWrite Write(string id, byte value)
    {
        using var monitor = Open(id, out var connectionProblem);
        if (monitor.IsInvalid)
        {
            return new VcpWrite(
                false,
                null,
                connectionProblem ?? UiText.Text("Không thể kết nối tới màn hình đã chọn.", "Could not connect to the selected monitor."));
        }

        var sent = SetVCPFeature(monitor, InputSourceVcpCode, value);
        if (!sent)
        {
            var error = Marshal.GetLastWin32Error();
            return new VcpWrite(false, error, null);
        }

        return new VcpWrite(true, null, null);
    }

    [DllImport("Dxva2.dll", SetLastError = true, EntryPoint = "SetVCPFeature")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(SafeHandle hMonitor, byte vcpCode, uint newValue);
}

public readonly record struct VcpWrite(bool Sent, int? Error, string? ConnectionError);
