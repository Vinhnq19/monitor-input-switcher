using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace MonitorInputSwitcher;

internal sealed class WindowsLanInput : IDisposable
{
    internal const int RawMessage = 0x00FF;
    internal const int SessionMessage = 0x02B1;
    private static readonly nuint InjectionTag = 0x4D495331;
    private readonly HookProc _keyboard;
    private readonly HookProc _mouse;
    private readonly HwndSource _input;
    private HwndSource? _parking;
    private nint _keyboardHook;
    private nint _mouseHook;

    public WindowsLanInput(Func<int, KeyHook, bool> keyboard, Func<int, MouseHook, bool> mouse, HwndSourceHook messages)
    {
        _keyboard = (code, message, data) =>
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<KeyHook>(data);
                if ((key.Flags & 0x10) == 0 && keyboard((int)message, key))
                {
                    return 1;
                }
            }
            return CallNextHookEx(0, code, message, data);
        };
        _mouse = (code, message, data) =>
        {
            if (code >= 0)
            {
                var pointer = Marshal.PtrToStructure<MouseHook>(data);
                if ((pointer.Flags & 1) == 0 && mouse((int)message, pointer))
                {
                    return 1;
                }
            }
            return CallNextHookEx(0, code, message, data);
        };
        _input = new HwndSource(new HwndSourceParameters("Monitor Input Switcher Input") { ParentWindow = new nint(-3) });
        _input.AddHook(messages);
        try
        {
            var raw = new RawRegistration { Page = 1, Usage = 2, Flags = 0x100, Target = _input.Handle };
            if (!RegisterRawInputDevices(ref raw, 1, (uint)Marshal.SizeOf<RawRegistration>()))
            {
                throw new Win32Exception();
            }
            _ = WTSRegisterSessionNotification(_input.Handle, 0);
            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(13, _keyboard, module, 0);
            _mouseHook = SetWindowsHookEx(14, _mouse, module, 0);
            if (_keyboardHook == 0 || _mouseHook == 0)
            {
                throw new Win32Exception();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static bool TryReadMouse(nint handle, out RawMouse mouse)
    {
        uint size = (uint)Marshal.SizeOf<RawPacket>();
        uint read = GetRawInputData(handle, 0x10000003, out var packet, ref size, (uint)Marshal.SizeOf<RawHeader>());
        mouse = packet.Mouse;
        return read != uint.MaxValue && read >= size && packet.Header.Kind == 0 && (mouse.Flags & 1) == 0;
    }

    public static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static int VirtualKey(int code, int flags) =>
        (flags & 4) != 0 ? code : (int)MapVirtualKey((uint)(code | ((flags & 1) != 0 ? 0xE000 : 0)), 3);
    public static void Lock() => _ = LockWorkStation();

    public static bool IsDefaultDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0)
        {
            return false;
        }
        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(desktop, 2, name, 128, out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public static bool IsFullScreen()
    {
        var window = GetForegroundWindow();
        if (window == 0 || window == GetShellWindow() || !GetWindowRect(window, out var rect))
        {
            return false;
        }
        var name = new StringBuilder(40);
        _ = GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW")
        {
            return false;
        }
        var screen = Forms.Screen.FromHandle(window);
        var bounds = screen.Bounds;
        return rect.Left == bounds.Left && rect.Top == bounds.Top && rect.Right == bounds.Right && rect.Bottom == bounds.Bottom;
    }

    public bool Park(Point point)
    {
        _parking = new HwndSource(new HwndSourceParameters("Monitor Input Switcher Cursor")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            Width = 1, Height = 1, PositionX = point.X, PositionY = point.Y
        });
        _parking.AddHook(HideCursor);
        if (!SetWindowPos(_parking.Handle, new nint(-1), point.X, point.Y, 1, 1, 0x0050) || !SetCursorPos(point.X, point.Y))
        {
            Unpark();
            return false;
        }
        _ = SetCursor(0);
        return true;
    }

    public void Unpark()
    {
        _parking?.Dispose();
        _parking = null;
    }

    public static Point CursorPosition() => GetCursorPos(out var point) ? point : Point.Empty;

    public static bool Position(Point point)
    {
        var virtualDesk = Forms.SystemInformation.VirtualScreen;
        int x = Normalize(point.X, virtualDesk.Left, virtualDesk.Width);
        int y = Normalize(point.Y, virtualDesk.Top, virtualDesk.Height);
        return Mouse(0xC001, 0, x, y);
    }

    public static bool Inject(LanInputEvent input) => input.Kind switch
    {
        LanInputKind.Move => Mouse(1, 0, input.A, input.B),
        LanInputKind.Key => Key(input.A, input.B),
        LanInputKind.Button => Button(input.A, input.B != 0),
        LanInputKind.Wheel => Mouse(input.B == 0 ? 0x0800u : 0x1000u, unchecked((uint)input.A)),
        _ => false
    };

    public static bool Key(int scan, int flags)
    {
        bool virtualKey = (flags & 4) != 0;
        var input = new NativeInput
        {
            Type = 1,
            Data = new InputData
            {
                Key = new KeyInput
                {
                    VirtualKey = virtualKey ? (ushort)scan : (ushort)0,
                    Scan = virtualKey ? (ushort)0 : (ushort)scan,
                    Flags = (uint)(flags & 3) | (virtualKey ? 0u : 8u),
                    Extra = InjectionTag
                }
            }
        };
        return SendInput(1, ref input, Marshal.SizeOf<NativeInput>()) == 1;
    }

    public static bool Button(int button, bool up)
    {
        uint flag = button switch { 0 => 2, 1 => 8, 2 => 32, _ => 128 };
        return Mouse(up ? flag << 1 : flag, button >= 3 ? (uint)(button - 2) : 0);
    }

    private static bool Mouse(uint flags, uint data, int dx = 0, int dy = 0)
    {
        var input = new NativeInput
        {
            Data = new InputData
            {
                Mouse = new MouseInput { X = dx, Y = dy, Flags = flags, Data = data, Extra = InjectionTag }
            }
        };
        return SendInput(1, ref input, Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static int Normalize(int value, int origin, int size) =>
        (int)Math.Clamp(((long)(value - origin) * 65536 + 32768) / Math.Max(size, 1), 0, 65535);

    private static nint HideCursor(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x20)
        {
            _ = SetCursor(0);
            handled = true;
            return 1;
        }
        return 0;
    }

    public void Dispose()
    {
        Unpark();
        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        var raw = new RawRegistration { Page = 1, Usage = 2, Flags = 1 };
        _ = RegisterRawInputDevices(ref raw, 1, (uint)Marshal.SizeOf<RawRegistration>());
        _ = WTSUnRegisterSessionNotification(_input.Handle);
        _input.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyHook { public uint VirtualKey, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHook { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawMouse { public ushort Flags; public ushort Padding; public ushort Buttons, Data; public uint RawButtons; public int X, Y; public uint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { public uint Kind, Size; public nint Device; public nuint Parameter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawPacket { public RawHeader Header; public RawMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawRegistration { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData { [FieldOffset(0)] public KeyInput Key; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate nint HookProc(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(ref RawRegistration devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, out RawPacket data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, ref NativeInput input, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint desktop, int index, StringBuilder value, uint size, out uint needed);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
}
