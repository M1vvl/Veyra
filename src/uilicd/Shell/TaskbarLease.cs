using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GlassDock.Shell;

/// <summary>
/// A reversible lease over Explorer's taskbar visibility. The independent watchdog restores the
/// original appbar state if the UI process is terminated. Explorer is never stopped or replaced.
/// </summary>
public sealed class TaskbarLease : IDisposable
{
    private const uint AbmGetState = 4, AbmSetState = 10, AbsAutoHide = 1;
    private const int SwHide = 0, SwShowNoActivate = 4;
    private static readonly string RecoveryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDock", "Recovery");
    private readonly object _sync = new();
    private Timer? _timer;
    private LeaseState? _state;
    private EventWaitHandle? _finished;
    private string? _statePath;
    private long _revealUntil;
    private bool _temporarilyRevealed;
    private bool _disposed;
    private int _enforceQueued;
    private bool _persistentRequested;
    private int _startSuppressionCount;
    private int _suppressionGeneration;
    private nint _taskbarShowHook;
    private readonly WinEventProc _taskbarShowCallback;
    public bool IsEnabled { get { lock (_sync) return _state is not null; } }

    public TaskbarLease() => _taskbarShowCallback = OnTaskbarShown;

    public void Enable()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureEnabled();
            _persistentRequested = true;
            EnsureShowHook();
            Enforce();
        }
    }

    internal IDisposable SuppressForStart()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureEnabled();
            _startSuppressionCount++;
            _revealUntil = 0;
            Enforce();
            EnsureShowHook();
            _timer?.Change(50, 50);
            return new StartSuppression(this, _suppressionGeneration);
        }
    }

    private void EnsureEnabled()
    {
        lock (_sync)
        {
            if (_state is not null) return;
            var bar = FindExplorerTaskbar();
            if (bar == IntPtr.Zero) throw new InvalidOperationException("未找到 Windows 资源管理器任务栏。");
            Directory.CreateDirectory(RecoveryDirectory);
            RestoreAbandonedLeases();
            var data = AppBarData.Create();
            string token = Guid.NewGuid().ToString("N");
            using var owner = Process.GetCurrentProcess();
            var state = new LeaseState(Environment.ProcessId, owner.StartTime.ToUniversalTime().Ticks,
                (uint)SHAppBarMessage(AbmGetState, ref data), token, CaptureVisibility());
            _statePath = Path.Combine(RecoveryDirectory, token + ".json");
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            _finished = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token));
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(token));
            try
            {
                StartWatchdog(state, _statePath);
                if (!ready.WaitOne(TimeSpan.FromSeconds(4))) throw new InvalidOperationException("任务栏恢复守护进程未就绪；保留 Windows 任务栏。");
                _state = state;
                data.Parameter = (nint)(state.AppBarState | AbsAutoHide);
                SHAppBarMessage(AbmSetState, ref data);
                SetTaskbarsVisible(false);
                _timer = new Timer(_ => Enforce(), null, 750, 750);
            }
            catch
            {
                _state = null;
                RestoreState(state);
                _finished.Set();
                _finished.Dispose();
                _finished = null;
                TryDelete(_statePath);
                _statePath = null;
                throw;
            }
        }
    }

    /// <summary>Keep the original tray available while the user interacts with native popup menus.</summary>
    public void RevealNativeTaskbar(TimeSpan? duration = null)
    {
        lock (_sync)
        {
            if (_startSuppressionCount > 0) return;
            _revealUntil = Environment.TickCount64 + (long)(duration ?? TimeSpan.FromSeconds(12)).TotalMilliseconds;
            if (_state is not null)
            {
                // ShowWindow alone cannot expand an autohidden taskbar whose window has slid
                // outside the monitor. Temporarily clear autohide, then reapply the lease later.
                var data = AppBarData.Create();
                data.Parameter = (nint)(_state.AppBarState & ~AbsAutoHide);
                SHAppBarMessage(AbmSetState, ref data);
                _temporarilyRevealed = true;
            }
            SetTaskbarsVisible(true);
        }
    }

    private void Enforce()
    {
        var dispatcher=System.Windows.Application.Current?.Dispatcher;
        if(dispatcher is not null && !dispatcher.CheckAccess())
        {
            if(dispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref _enforceQueued,1,0)!=0) return;
            dispatcher.BeginInvoke(new Action(()=> { try { Enforce(); } finally { Volatile.Write(ref _enforceQueued,0); } }),System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        lock (_sync)
        {
            if (_state is null || _disposed) return;
            try
            {
                if (_finished?.WaitOne(0) == true)
                {
                    _persistentRequested = false;
                    _startSuppressionCount = 0;
                    _suppressionGeneration++;
                    StopShowHook();
                    ReleaseLease();
                    return;
                }
                if (_startSuppressionCount == 0 && Environment.TickCount64 < _revealUntil) return;
                // Native tray menus and overflow remain fully usable, even after the grace period.
                if (_startSuppressionCount == 0 && _temporarilyRevealed && (NativeTrayPopupIsVisible() || CursorIsOverTaskbar())) return;
                if (_temporarilyRevealed)
                {
                    var data = AppBarData.Create();
                    data.Parameter = (nint)(_state.AppBarState | AbsAutoHide);
                    SHAppBarMessage(AbmSetState, ref data);
                    _temporarilyRevealed = false;
                }
                SetTaskbarsVisible(false);
            }
            catch (Exception ex) { Trace.WriteLine("Taskbar lease: " + ex.Message); }
        }
    }

    public void Disable()
    {
        lock (_sync)
        {
            _persistentRequested = false;
            if (_startSuppressionCount > 0) return;
            ReleaseLease();
        }
    }

    private void ReleaseLease()
    {
        lock (_sync)
        {
            StopShowHook();
            _timer?.Dispose();
            _timer = null;
            if (_state is not null) RestoreState(_state);
            _state = null;
            _temporarilyRevealed = false;
            _finished?.Set();
            _finished?.Dispose();
            _finished = null;
            if (_statePath is not null) TryDelete(_statePath);
            _statePath = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _persistentRequested = false;
            _startSuppressionCount = 0;
            _suppressionGeneration++;
            StopShowHook();
            ReleaseLease();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void EndStartSuppression(int generation)
    {
        lock (_sync)
        {
            if (_disposed || generation != _suppressionGeneration || _startSuppressionCount == 0) return;
            _startSuppressionCount--;
            if (_startSuppressionCount != 0) return;
            if (_persistentRequested) {EnsureShowHook(); _timer?.Change(750, 750);}
            else ReleaseLease();
        }
    }

    private void OnTaskbarShown(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == 0 || objectId != 0 || childId != 0) return;
        lock (_sync)
        {
            if (_disposed || _state is null || (!_persistentRequested && _startSuppressionCount == 0)) return;
            if (_finished?.WaitOne(0) == true) return;
            if(_startSuppressionCount==0 && (Environment.TickCount64<_revealUntil || _temporarilyRevealed && NativeTrayPopupIsVisible()))return;
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsExplorerWindow(window))
                ShowWindowAsync(window, SwHide);
        }
    }
    private void EnsureShowHook()
    {
        if(_taskbarShowHook==0)_taskbarShowHook=SetWinEventHook(0x8002,0x8002,0,_taskbarShowCallback,0,0,2);
    }

    private void StopShowHook()
    {
        if (_taskbarShowHook != 0) UnhookWinEvent(_taskbarShowHook);
        _taskbarShowHook = 0;
        GC.KeepAlive(_taskbarShowCallback);
    }

    private sealed class StartSuppression(TaskbarLease owner, int generation) : IDisposable
    {
        private TaskbarLease? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndStartSuppression(generation);
    }

    /// <summary>Call before creating WPF windows. Returns true when this invocation was the helper.</summary>
    public static bool RunWatchdogIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] != "--watchdog") return false;
        if (args.Length != 3 || !int.TryParse(args[1], out int pid)) return true;
        string fullPath = Path.GetFullPath(args[2]);
        string expectedRoot = Path.GetFullPath(RecoveryDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(fullPath) != ".json") return true;
        try
        {
            var state = JsonSerializer.Deserialize<LeaseState>(File.ReadAllText(fullPath));
            if (state is null || state.ProcessId != pid || Path.GetFileNameWithoutExtension(fullPath) != state.Token) return true;
            using var ready = EventWaitHandle.OpenExisting(ReadyName(state.Token));
            using var finished = EventWaitHandle.OpenExisting(EventName(state.Token));
            ready.Set();
            while (!finished.WaitOne(500))
            {
                if (!OwnerIsAlive(state))
                {
                    RestoreState(state);
                    TryDelete(fullPath);
                    break;
                }
            }
        }
        catch (Exception ex) { Trace.WriteLine("Taskbar watchdog: " + ex.Message); }
        return true;
    }

    /// <summary>Manual recovery entry point, also used at startup to restore an abandoned lease.</summary>
    public static void RestoreAbandonedLeases()
    {
        if (!Directory.Exists(RecoveryDirectory)) return;
        foreach (string path in Directory.EnumerateFiles(RecoveryDirectory, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<LeaseState>(File.ReadAllText(path));
                if (state is not null && !OwnerIsAlive(state)) { RestoreState(state); TryDelete(path); }
            }
            catch (Exception ex) { Trace.WriteLine("Recovery: " + ex.Message); }
        }
    }

    public static void ShowWindowsTaskbar() => SetTaskbarsVisible(true);
    public static object InspectTaskbar()
    {
        var data=AppBarData.Create(); var window=FindExplorerTaskbar();
        return new { AppBarState=(uint)SHAppBarMessage(AbmGetState,ref data), Visible=window!=IntPtr.Zero && IsWindowVisible(window) };
    }

    /// <summary>Restore even an active lease; the running UI observes the event and stops hiding.</summary>
    public static void EmergencyRestore()
    {
        if (Directory.Exists(RecoveryDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(RecoveryDirectory, "*.json"))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<LeaseState>(File.ReadAllText(path));
                    if (state is null) continue;
                    try { using var finished = EventWaitHandle.OpenExisting(EventName(state.Token)); finished.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    RestoreState(state);
                    TryDelete(path);
                }
                catch (Exception ex) { Trace.WriteLine("Emergency recovery: " + ex.Message); }
            }
        }
        SetTaskbarsVisible(true);
    }

    /// <summary>No taskbar, UIAutomation, keyboard, mouse, or appbar mutations.</summary>
    public static IReadOnlyList<string> VerifyRecoveryContracts()
    {
        var failures = new List<string>();
        int expected = IntPtr.Size == 8 ? 48 : 36;
        if (Marshal.SizeOf<AppBarData>() != expected) failures.Add("APPBARDATA native layout mismatch");
        using var owner = Process.GetCurrentProcess();
        var state = new LeaseState(Environment.ProcessId, owner.StartTime.ToUniversalTime().Ticks, 3, Guid.NewGuid().ToString("N"));
        var decoded = JsonSerializer.Deserialize<LeaseState>(JsonSerializer.Serialize(state));
        if (decoded != state) failures.Add("Recovery state JSON round-trip failed");
        if (!OwnerIsAlive(state)) failures.Add("Owner identity must match PID and process start time");
        if (OwnerIsAlive(state with { StartTimeUtcTicks = state.StartTimeUtcTicks - 1 })) failures.Add("PID reuse guard failed");
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(state.Token));
        using var opened = EventWaitHandle.OpenExisting(EventName(state.Token));
        opened.Set();
        if (!signal.WaitOne(0)) failures.Add("Watchdog signal cannot be reopened by name");
        return failures;
    }

    private static bool OwnerIsAlive(LeaseState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.StartTimeUtcTicks;
        }
        catch { return false; }
    }

    private static void StartWatchdog(LeaseState state, string path)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 Dock 程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"uilicd.dll"));
        start.ArgumentList.Add("--watchdog");
        start.ArgumentList.Add(state.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动任务栏恢复守护进程。");
    }

    private static void RestoreState(LeaseState state)
    {
        var data = AppBarData.Create();
        data.Parameter = (nint)state.AppBarState;
        SHAppBarMessage(AbmSetState, ref data);
        ForEachTaskbar(window =>
        {
            var saved = state.Taskbars?.FirstOrDefault(item => item.Window == window.ToInt64());
            ShowWindowAsync(window, saved?.Visible == false ? SwHide : SwShowNoActivate);
        });
    }

    private static TaskbarVisibility[] CaptureVisibility()
    {
        var bars = new List<TaskbarVisibility>();
        ForEachTaskbar(window => bars.Add(new TaskbarVisibility(window.ToInt64(), IsWindowVisible(window))));
        return bars.ToArray();
    }

    private static bool CursorIsOverTaskbar()
    {
        if (!GetCursorPos(out var point)) return false;
        bool inside = false;
        ForEachTaskbar(window =>
        {
            if (IsWindowVisible(window) && GetWindowRect(window, out var rect) &&
                point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom) inside = true;
        });
        return inside;
    }

    private static bool NativeTrayPopupIsVisible()
    {
        foreach (string className in new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "#32768" })
        {
            var handle = FindWindow(className, null);
            if (handle != IntPtr.Zero && IsWindowVisible(handle)) return true;
        }
        return false;
    }

    private static void SetTaskbarsVisible(bool visible) => ForEachTaskbar(window => {
        if(IsWindowVisible(window)!=visible) ShowWindowAsync(window, visible ? SwShowNoActivate : SwHide);
    });
    private static void ForEachTaskbar(Action<IntPtr> action)
    {
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsExplorerWindow(window)) action(window);
            return true;
        }, IntPtr.Zero);
    }

    internal static nint FindExplorerTaskbar()
    {
        nint result=0;
        EnumWindows((window,_)=> {
            var name=new StringBuilder(128); GetClassName(window,name,name.Capacity);
            if(name.ToString()!="Shell_TrayWnd" || !IsExplorerWindow(window)) return true;
            result=window; return false;
        },0);
        return result;
    }
    private static bool IsExplorerWindow(nint window)
    {
        GetWindowThreadProcessId(window,out uint pid);
        GetWindowThreadProcessId(GetShellWindow(),out uint shellPid);
        if(pid!=0 && shellPid!=0) return pid==shellPid;
        try { using var process=Process.GetProcessById((int)pid); return string.Equals(process.MainModule?.FileName,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) { Trace.WriteLine(ex.Message); } }
    private static string EventName(string token) => @"Local\GlassDock.Lease." + token;
    private static string ReadyName(string token) => @"Local\GlassDock.Ready." + token;
    private sealed record LeaseState(int ProcessId, long StartTimeUtcTicks, uint AppBarState, string Token, TaskbarVisibility[]? Taskbars = null);
    private sealed record TaskbarVisibility(long Window, bool Visible);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size; public IntPtr Window; public uint CallbackMessage, Edge; public Rect Rect; public nint Parameter;
        public static AppBarData Create() => new() { Size = (uint)Marshal.SizeOf<AppBarData>() };
    }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
}
