using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MonitorInputSwitcher;

internal sealed class LanFileClipboard : ILanFileClipboard, IAsyncDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _log;
    private LanFileChannel? _channel;
    private LanFileChannel.Selection? _local;
    private RemoteSelection? _remote;
    private ExplorerDestination? _destination;
    private uint _observed;
    private long _lastReport;
    private long _lastPasteSkip;
    private static readonly uint PreferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");

    private sealed record RemoteSelection(LanFileChannel Channel, LanFileChannel.Offer Offer, uint Sequence);
    private sealed record ExplorerDestination(nint Window, nint View, string Directory);

    public LanFileClipboard(Action<string> log)
    {
        _log = log;
        var thread = new Thread(ThreadMain) { IsBackground = true, Name = "LAN file clipboard (STA)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public LanFileChannel.Selection? LocalSelection
    {
        get
        {
            var value = Volatile.Read(ref _local);
            return value is not null && IsCurrent(value) ? value : null;
        }
    }

    public bool IsCurrent(LanFileChannel.Selection selection) =>
        ReferenceEquals(Volatile.Read(ref _local), selection) && GetClipboardSequenceNumber() == selection.ClipboardSequence;

    public void Report(string code, Exception? error = null, int count = 0, long bytes = 0)
    {
        if (code == "channel")
        {
            long previous = Interlocked.Read(ref _lastReport);
            long now = Stopwatch.GetTimestamp();
            if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(1)) return;
            Interlocked.Exchange(ref _lastReport, now);
        }
        string? detail = error is LanFileFailure failure ? failure.Code : error?.GetType().Name;
        _log($"LAN files {code} {detail} 0x{error?.HResult:X8} {count} items {bytes} bytes");
    }

    public async Task RunAsync(Stream stream, CancellationToken stop)
    {
        var channel = new LanFileChannel(stream, this, stop);
        var dispatcher = await _ready.Task.WaitAsync(stop).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _channel, channel, null) is not null) return;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                _observed = GetClipboardSequenceNumber();
                Volatile.Write(ref _local, null);
            }, DispatcherPriority.Send, stop).Task.ConfigureAwait(false);
            Report("channel-ready");
            await channel.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _channel, null, channel);
            _ = dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _remote)?.Channel == channel) SetRemote(null);
            });
        }
    }

    public void ReceiveOffer(LanFileChannel channel, LanFileChannel.Offer? offer)
    {
        _ready.Task.Result.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (!ReferenceEquals(channel, Volatile.Read(ref _channel))) return;
            ReadClipboard();
            SetRemote(offer is null ? null : new RemoteSelection(channel, offer, _observed));
            if (offer is not null) Report("offer-received");
        });
    }

    public void Pasted(LanFileChannel channel, Guid id)
    {
        _ready.Task.Result.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _remote) is { } current && current.Channel == channel &&
                current.Offer.Id == id && current.Offer.Move) SetRemote(null);
        });
    }

    public bool TryPaste()
    {
        var channel = Volatile.Read(ref _channel);
        nint window = GetForegroundWindow();
        if (!IsExplorerWindow(window)) return false;
        var remote = Volatile.Read(ref _remote);
        if (channel is null || remote is null || remote.Channel != channel)
        {
            ReportPasteSkip(channel is null ? "paste-no-channel" : "paste-no-offer");
            return false;
        }
        var destination = Volatile.Read(ref _destination);
        if (destination is null || destination.Window != window || !ViewHasFocus(destination.View))
        {
            ReportPasteSkip("paste-no-folder");
            return false;
        }
        if (remote.Sequence != GetClipboardSequenceNumber())
        {
            ReportPasteSkip("paste-clipboard-changed");
            return false;
        }
        _ready.Task.Result.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (ReferenceEquals(remote, _remote) && remote.Channel == Volatile.Read(ref _channel) &&
                remote.Sequence == GetClipboardSequenceNumber() && destination == FindDestination())
            {
                Report("paste-requested");
                _ = remote.Channel.PasteAsync(remote.Offer, destination.Directory);
            }
            else Report("paste-context-changed");
        });
        return true;
    }

    private void ReportPasteSkip(string code)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastPasteSkip);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return;
        if (Interlocked.CompareExchange(ref _lastPasteSkip, now, previous) == previous &&
            _ready.Task.IsCompletedSuccessfully)
        {
            _ = _ready.Task.Result.BeginInvoke(DispatcherPriority.Background, () => Report(code));
        }
    }

    public async Task<bool> RecycleAsync(LanFileChannel.Selection selection,
        LanSourceEntry[] tree, CancellationToken stop)
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrent(selection)) return false;
            foreach (string path in selection.Paths)
            {
                stop.ThrowIfCancellationRequested();
                var expected = tree.Where(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    item.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!expected.SequenceEqual(LanFileTree.Scan([path])) ||
                    !ShellRecycle.Recycle(path)) return false;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _local, null, selection), selection))
                Volatile.Read(ref _channel)?.PublishClipboard();
            return true;
        }, DispatcherPriority.Normal, stop).Task.ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) =>
        {
            ReadClipboard();
            Volatile.Write(ref _destination, _remote is null ? null : FindDestination());
        };
        try
        {
            using var window = new HwndSource(new HwndSourceParameters("Monitor Input Switcher Clipboard")
            {
                ParentWindow = new nint(-3)
            });
            window.AddHook(Message);
            _ = AddClipboardFormatListener(window.Handle);
            _observed = GetClipboardSequenceNumber();
            _ready.SetResult(dispatcher);
            timer.Start();
            try { Dispatcher.Run(); }
            finally
            {
                timer.Stop();
                _ = RemoveClipboardFormatListener(window.Handle);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ready.TrySetException(ex);
            Report("clipboard", ex);
        }
        finally { _stopped.TrySetResult(); }
    }

    private nint Message(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x031D) ReadClipboard();
        return 0;
    }

    private void ReadClipboard()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _observed) return;
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
        {
            _observed = sequence;
            return;
        }
        LanFileChannel.Selection? selection = null;
        if (!OpenClipboard(0)) return;
        try
        {
            if (IsExplorer(GetClipboardOwner()) && GetClipboardData(15) is var drop && drop != 0)
            {
                uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
                if (count is > 0 and <= LanFileTree.EntryLimit)
                {
                    uint effect = 1;
                    nint effectHandle = PreferredDropEffect == 0 ? 0 : GetClipboardData(PreferredDropEffect);
                    if (effectHandle != 0 && GlobalSize(effectHandle) >= 4)
                    {
                        nint pointer = GlobalLock(effectHandle);
                        if (pointer != 0)
                        {
                            try { effect = (uint)Marshal.ReadInt32(pointer); }
                            finally { _ = GlobalUnlock(effectHandle); }
                        }
                    }
                    if (effect is 1 or 2 or 5)
                    {
                        var paths = new string[count];
                        for (uint index = 0; index < count; index++)
                        {
                            uint length = DragQueryFileW(drop, index, null, 0);
                            if (length is 0 or > 32767) throw new LanFileFailure("clipboard-path");
                            var path = new StringBuilder((int)length + 1);
                            if (DragQueryFileW(drop, index, path, length + 1) != length) throw new LanFileFailure("clipboard-path");
                            paths[index] = path.ToString();
                        }
                        selection = new(new(Guid.NewGuid(), effect == 2), paths, sequence);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report("clipboard-read", ex); }
        finally { _ = CloseClipboard(); }
        if (GetClipboardSequenceNumber() != sequence) return;
        _observed = sequence;
        if (_remote is not null)
        {
            SetRemote(null);
            Report("offer-cleared-by-local-clipboard");
        }
        Volatile.Write(ref _local, selection);
        if (selection is not null) Report("offer-prepared", count: selection.Paths.Length);
        channel.PublishClipboard();
    }

    private void SetRemote(RemoteSelection? remote)
    {
        Volatile.Write(ref _destination, null);
        Volatile.Write(ref _remote, remote);
        if (remote is not null) Volatile.Write(ref _destination, FindDestination());
    }

    private static ExplorerDestination? FindDestination()
    {
        nint window = GetForegroundWindow();
        if (!IsExplorerWindow(window)) return null;
        object? shell = null;
        object? windows = null;
        try
        {
            if (!IsExplorer(window)) return null;
            var type = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            shell = Activator.CreateInstance(type)!;
            windows = ((dynamic)shell).Windows();
            ExplorerDestination? result = null;
            foreach (object candidate in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    if ((nint)(long)((dynamic)candidate).HWND != window || candidate is not Services services) continue;
                    Guid service = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
                    Guid iid = typeof(ShellBrowser).GUID;
                    if (services.QueryService(ref service, ref iid, out object browser) < 0) continue;
                    try
                    {
                        if (((ShellBrowser)browser).QueryActiveShellView(out ShellView view) < 0) continue;
                        try
                        {
                            if (view.GetWindow(out nint viewWindow) < 0 || !ViewHasFocus(viewWindow)) continue;
                            string directory = ((dynamic)candidate).Document.Folder.Self.Path;
                            if (!Path.IsPathFullyQualified(directory) || directory.Length < 3 || directory[1] != ':') continue;
                            var resolved = new ExplorerDestination(window, viewWindow, Path.GetFullPath(directory));
                            if (result is not null && result != resolved) return null;
                            result = resolved;
                        }
                        finally { Release(view); }
                    }
                    finally { Release(browser); }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                finally { Release(candidate); }
            }
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        finally
        {
            Release(windows);
            Release(shell);
        }
    }

    private static bool IsExplorerWindow(nint window) =>
        window != 0 && ClassName(window) is "CabinetWClass" or "ExploreWClass";

    private static bool ViewHasFocus(nint view)
    {
        var state = new GuiThread { Size = Marshal.SizeOf<GuiThread>() };
        if (view == 0 || !GetGUIThreadInfo(0, ref state) || state.Focus == 0 ||
            state.Focus != view && !IsChild(view, state.Focus)) return false;
        string name = ClassName(state.Focus);
        return name != "Edit" && !name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(128);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static bool IsExplorer(nint window)
    {
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out int pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.MainModule?.FileName, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (await _ready.Task.ConfigureAwait(false) is { HasShutdownStarted: false } dispatcher)
            await dispatcher.InvokeAsync(() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send)).Task.ConfigureAwait(false);
        await _stopped.Task.ConfigureAwait(false);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Services
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    // QueryActiveShellView supplies IShellView, whose first method is GetWindow.
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShellView { [PreserveSig] int GetWindow(out nint hwnd); }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShellBrowser
    {
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int InsertMenusSB(nint menu, nint widths);
        [PreserveSig] int SetMenuSB(nint shared, nint ole, nint active);
        [PreserveSig] int RemoveMenusSB(nint menu);
        [PreserveSig] int SetStatusTextSB(nint text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView(out ShellView view);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThread
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public System.Drawing.Rectangle CaretRectangle;
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? path, uint capacity);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThread state);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int size);
}
