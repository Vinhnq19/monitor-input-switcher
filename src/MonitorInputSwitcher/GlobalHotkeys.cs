using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorInputSwitcher;

/// <summary>
/// Owns the global hotkey registrations (WM_HOTKEY) for the three input actions.
/// All members must be used from the dispatcher thread that owns the window handle.
/// </summary>
public sealed class GlobalHotkeys(nint windowHandle) : IDisposable
{
    public const int MessageId = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint SupportedModifiers = ModAlt | ModControl | ModShift | ModWin;
    private const uint ModNoRepeat = 0x4000;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private static readonly InputAction[] SupportedActions =
    [
        InputAction.Pc,
        InputAction.Laptop,
        InputAction.Toggle
    ];

    private readonly nint _windowHandle = windowHandle;
    private readonly Dictionary<InputAction, Registration> _active = new();

    private bool _disposed;

    /// <summary>
    /// Attempts to install every hotkey independently. Hotkeys that register successfully are
    /// retained; failures are returned as localized messages and do not cancel the other actions.
    /// </summary>
    public IReadOnlyList<string> InstallInitial(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        if (_disposed)
        {
            errors.Add(UiText.Text("Bộ quản lý phím tắt đã được giải phóng.", "The hotkey manager has been disposed."));
            return errors;
        }

        var validation = settings.Validate();
        if (validation is not null)
        {
            errors.Add(validation);
            return errors;
        }

        foreach (var action in SupportedActions)
        {
            var chord = settings.GetHotkey(action);

            if (_active.TryGetValue(action, out var existing))
            {
                if (existing.Chord == chord)
                {
                    continue;
                }

                if (!TryUnregister(action, out var unregisterError))
                {
                    errors.Add(unregisterError!);
                    continue;
                }
            }

            if (!TryRegister(action, chord, out var registerError))
            {
                errors.Add(registerError!);
            }
        }

        return errors;
    }

    /// <summary>
    /// Applies a new hotkey configuration transactionally: unchanged bindings are kept, changed
    /// bindings are released before any new registration happens (so swaps work), and any failure
    /// rolls the whole call back to the previously active mapping.
    /// </summary>
    public bool TryApply(AppSettings settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_disposed)
        {
            error = UiText.Text("Bộ quản lý phím tắt đã được giải phóng.", "The hotkey manager has been disposed.");
            return false;
        }

        var validation = settings.Validate();
        if (validation is not null)
        {
            error = validation;
            return false;
        }

        var desired = new Dictionary<InputAction, HotkeyChord>(SupportedActions.Length)
        {
            [InputAction.Pc] = settings.GetHotkey(InputAction.Pc),
            [InputAction.Laptop] = settings.GetHotkey(InputAction.Laptop),
            [InputAction.Toggle] = settings.GetHotkey(InputAction.Toggle)
        };

        var toUnregister = new List<Registration>();
        foreach (var registration in _active.Values)
        {
            if (desired[registration.Action] != registration.Chord)
            {
                toUnregister.Add(registration);
            }
        }

        var removed = new List<Registration>(toUnregister.Count);
        foreach (var registration in toUnregister)
        {
            var action = registration.Action;
            if (!TryUnregister(action, out var unregisterError))
            {
                var restoreFailures = RestoreRegistrations(removed);
                error = DescribeRollback(unregisterError!, restoreFailures);
                return false;
            }

            removed.Add(registration);
        }

        var added = new List<Registration>(SupportedActions.Length);
        foreach (var action in SupportedActions)
        {
            var chord = desired[action];
            if (_active.TryGetValue(action, out var existing) && existing.Chord == chord)
            {
                continue;
            }

            if (!TryRegister(action, chord, out var registerError))
            {
                var rollbackFailures = new List<string>();

                foreach (var registration in added)
                {
                    if (!_active.ContainsKey(registration.Action))
                    {
                        continue;
                    }

                    if (!TryUnregister(registration.Action, out var undoError))
                    {
                        rollbackFailures.Add(undoError!);
                    }
                }

                rollbackFailures.AddRange(RestoreRegistrations(removed));
                error = DescribeRollback(registerError!, rollbackFailures);
                return false;
            }

            added.Add(_active[action]);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates a WM_HOTKEY payload against the currently active registration so a stale message
    /// queued for an older chord is ignored.
    /// </summary>
    public bool TryGetAction(nint messageWParam, nint messageLParam, out InputAction action)
    {
        action = default;

        var identifier = (long)messageWParam;
        if (identifier <= 0 || identifier > 0xBFFF)
        {
            return false;
        }

        var registration = FindById((int)identifier);
        if (registration is null)
        {
            return false;
        }

        var payload = unchecked((uint)(long)messageLParam);
        var modifiers = payload & 0xFFFF & SupportedModifiers;
        var virtualKey = (payload >> 16) & 0xFFFF;

        if (modifiers != registration.Chord.Modifiers || virtualKey != registration.Chord.VirtualKey)
        {
            return false;
        }

        action = registration.Action;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var registrations = new List<Registration>(_active.Values);
        foreach (var registration in registrations)
        {
            // Best effort: nothing to report during disposal, but stop dispatching either way.
            UnregisterHotKey(_windowHandle, registration.Id);
            _active.Remove(registration.Action);
        }
    }

    private static int IdFor(InputAction action) => action switch
    {
        InputAction.Pc => 0xB001,
        InputAction.Laptop => 0xB002,
        InputAction.Toggle => 0xB003,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, UiText.Text("Hành động chuyển đầu vào không hợp lệ.", "Invalid input switching action."))
    };

    private static string ActionName(InputAction action) => action switch
    {
        InputAction.Pc => "PC",
        InputAction.Laptop => "laptop",
        InputAction.Toggle => UiText.Text("chuyển đổi", "toggle"),
        _ => action.ToString()
    };

    private static string DescribeRegisterFailure(InputAction action, HotkeyChord chord, int errorCode)
    {
        var reason = errorCode == ErrorHotkeyAlreadyRegistered
            ? UiText.Text("tổ hợp phím đang được ứng dụng khác sử dụng (ERROR_HOTKEY_ALREADY_REGISTERED = 1409)", "another application is using this combination (ERROR_HOTKEY_ALREADY_REGISTERED = 1409)")
            : (UiText.IsEnglish ? $"{new Win32Exception(errorCode).Message} (error {errorCode})" : $"{new Win32Exception(errorCode).Message} (mã {errorCode})");

        return (UiText.IsEnglish ? $"Could not register {chord} for {ActionName(action)}: {reason}." : $"Không thể đăng ký phím tắt {chord} cho thao tác {ActionName(action)}: {reason}.");
    }

    private static string DescribeUnregisterFailure(Registration registration, int errorCode) =>
        (UiText.IsEnglish ? $"Could not unregister {registration.Chord} for {ActionName(registration.Action)}: " : $"Không thể gỡ phím tắt {registration.Chord} của thao tác {ActionName(registration.Action)}: ") +
        (UiText.IsEnglish ? $"{new Win32Exception(errorCode).Message} (error {errorCode})." : $"{new Win32Exception(errorCode).Message} (mã {errorCode}).");

    private static string DescribeRollback(string primaryError, List<string> restoreFailures)
    {
        if (restoreFailures.Count == 0)
        {
            return (UiText.IsEnglish ? $"{primaryError} The previous hotkeys were restored." : $"{primaryError} Đã khôi phục cấu hình phím tắt trước đó.");
        }

        return (UiText.IsEnglish ? $"{primaryError} Some previous hotkeys could not be restored: " : $"{primaryError} Không thể khôi phục hoàn toàn cấu hình phím tắt trước đó: ") +
               string.Join(" ", restoreFailures);
    }

    private bool TryRegister(InputAction action, HotkeyChord chord, out string? error)
    {
        var id = IdFor(action);

        if (!RegisterHotKey(_windowHandle, id, chord.Modifiers | ModNoRepeat, chord.VirtualKey))
        {
            var errorCode = Marshal.GetLastWin32Error();
            error = DescribeRegisterFailure(action, chord, errorCode);
            return false;
        }

        _active[action] = new Registration(action, chord, id);
        error = null;
        return true;
    }

    private bool TryUnregister(InputAction action, out string? error)
    {
        if (!_active.TryGetValue(action, out var registration))
        {
            error = null;
            return true;
        }

        if (!UnregisterHotKey(_windowHandle, registration.Id))
        {
            var errorCode = Marshal.GetLastWin32Error();
            error = DescribeUnregisterFailure(registration, errorCode);
            return false;
        }

        _active.Remove(action);
        error = null;
        return true;
    }

    private List<string> RestoreRegistrations(List<Registration> registrations)
    {
        var failures = new List<string>();

        foreach (var registration in registrations)
        {
            if (_active.ContainsKey(registration.Action))
            {
                continue;
            }

            if (!TryRegister(registration.Action, registration.Chord, out var restoreError))
            {
                failures.Add(restoreError!);
            }
        }

        return failures;
    }

    private Registration? FindById(int id)
    {
        foreach (var registration in _active.Values)
        {
            if (registration.Id == id)
            {
                return registration;
            }
        }

        return null;
    }

    private sealed record Registration(InputAction Action, HotkeyChord Chord, int Id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
