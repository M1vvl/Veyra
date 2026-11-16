using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace GlassDock.Shell;

/// <summary>
/// A bounded, out-of-process placement request for the real Windows Start window.
/// Full-screen hosts require real native content anchors and position readback;
/// guessing their visible panel from the full HWND would misalign hit testing.
/// </summary>
public static class StartMenuPlacement
{
    private static Request? _request;
    private static PlacementReport _report = new() { Status = "idle" };

    /// <param name="physicalDockSurface">The Dock Start button bounds in physical screen pixels.</param>
    public static void Toggle(Window owner, Rect physicalDockSurface)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        if (physicalDockSurface.IsEmpty || !double.IsFinite(physicalDockSurface.Left)
            || !double.IsFinite(physicalDockSurface.Top) || !double.IsFinite(physicalDockSurface.Width)
            || !double.IsFinite(physicalDockSurface.Height) || physicalDockSurface.Width <= 0
            || physicalDockSurface.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalDockSurface));

        if(_request is { } existing)
        {
            existing.ToggleClosed();
            return;
        }

        var request = new Request(owner, physicalDockSurface);
        _request = request;
        try
        {
            request.Start();
            ShellService.Start();
        }
        catch (Exception ex)
        {
            request.Finish("failed", false, ex.Message);
            throw;
        }
    }

    /// <summary>Stops this invocation's observer. Call on the application's UI thread at exit.</summary>
    public static void Cancel()
    {
        var request = _request;
        if (request is null) return;
        if (request.Dispatcher.CheckAccess()) request.Finish("cancelled", null, null);
        else if (!request.Dispatcher.HasShutdownStarted)
            request.Dispatcher.BeginInvoke(new Action(() => request.Finish("cancelled", null, null)));
    }

    /// <summary>Reports the observed result; a successful Win-key injection alone is not placement success.</summary>
    public static object Inspect() => _report;

    /// <summary>Read-only inventory used while the user has opened the native menu.</summary>
    public static object CaptureNativeStart()
    {
        var processes=new List<object>();
        var ids=new HashSet<uint>();
        foreach(var process in Process.GetProcessesByName("StartMenuExperienceHost"))
        {
            using(process)
            {
                bool recognized=IsSystemStartProcess((uint)process.Id);
                processes.Add(new { process.Id,recognized });
                if(recognized) ids.Add((uint)process.Id);
            }
        }
        var windows=new List<object>();
        foreach(var window in NativeStartWindows.Enumerate(ids))
        {
            GetWindowThreadProcessId(window,out uint process);
            var className=new StringBuilder(128);
            GetClassName(window,className,className.Capacity);
            GetWindowRect(window,out var bounds);
            DwmGetWindowAttribute(window,14,out int cloaked,sizeof(int));
            object? automation=null;
            string? error=null;
            try
            {
                var root=AutomationElement.FromHandle(window);
                var elements=new List<object>();
                CaptureAutomation(root,elements,0);
                var anchor=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"TopLevelRoot"));
                for(int depth=0;anchor is not null && depth<8;depth++)
                {
                    var current=anchor.Current; var rectangle=current.BoundingRectangle;
                    elements.Add(new { ancestor=depth,current.AutomationId,current.ClassName,
                        bounds=rectangle.IsEmpty?null:new { rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height },
                        patterns=anchor.GetSupportedPatterns().Select(pattern=>pattern.ProgrammaticName).ToArray() });
                    if(anchor==root)break;
                    anchor=TreeWalker.RawViewWalker.GetParent(anchor);
                }
                automation=elements;
            }
            catch(Exception exception) when(exception is ElementNotAvailableException or COMException or InvalidOperationException)
            { error=exception.Message; }
            windows.Add(new { window=window.ToInt64(),process,className=className.ToString(),visible=IsWindowVisible(window),cloaked,
                bounds=new { bounds.Left,bounds.Top,bounds.Right,bounds.Bottom },automation,error });
        }
        var foreground=GetForegroundWindow();
        GetWindowThreadProcessId(foreground,out uint foregroundProcess);
        var foregroundClass=new StringBuilder(128); GetClassName(foreground,foregroundClass,foregroundClass.Capacity);
        return new { timestamp=DateTimeOffset.Now,processes,windows,foreground=new { window=foreground.ToInt64(),process=foregroundProcess,className=foregroundClass.ToString() } };
    }

    private static void CaptureAutomation(AutomationElement element,List<object> result,int depth)
    {
        if(result.Count>=80) return;
        var value=element.Current;
        var bounds=value.BoundingRectangle;
        result.Add(new { depth,value.AutomationId,value.ClassName,value.Name,type=value.ControlType?.ProgrammaticName,
            value.NativeWindowHandle,value.IsOffscreen,bounds=bounds.IsEmpty?null:new { bounds.X,bounds.Y,bounds.Width,bounds.Height } });
        if(depth>=3) return;
        foreach(AutomationElement child in element.FindAll(TreeScope.Children,System.Windows.Automation.Condition.TrueCondition))
        {
            CaptureAutomation(child,result,depth+1);
            if(result.Count>=80) break;
        }
    }

    private sealed record PlacementReport
    {
        public bool Active { get; init; }
        public string Status { get; init; } = "idle";
        public bool? Compatible { get; init; }
        public string? Detail { get; init; }
        public long Window { get; init; }
        public int Attempts { get; init; }
        public int EventHooks { get; init; }
        public Rect DockSurface { get; init; }
        public Rect? OriginalBounds { get; init; }
        public Rect? RequestedBounds { get; init; }
        public Rect? ActualBounds { get; init; }
        public Rect? OriginalContent { get; init; }
        public Rect? RequestedContent { get; init; }
        public Rect? ActualContent { get; init; }
    }

    private sealed class Request : IDisposable
    {
        private const long ObservationMilliseconds = 5000;
        private readonly Window _owner;
        private readonly Rect _dock;
        private readonly DispatcherTimer _timer;
        private readonly WinEventProc _eventCallback;
        private readonly HashSet<uint> _startProcesses = new();
        private readonly List<nint> _windows = new();
        private readonly EventHandler _ownerClosed;
        private nint _showHook, _locationHook;
        private long _deadline, _refreshProcessesAt, _lastMoveAt, _settleAt;
        private nint _window;
        private IDisposable? _taskbarSuppression;
        private Rect? _original, _requested, _actual;
        private Rect? _originalContent,_requestedContent,_actualContent;
        private bool _closing, _seenWindow, _disposed, _tickQueued, _verified, _placementComplete;
        private int _attempts, _confirmedReads;
        private string _unsupportedReason = "尚未观察到 Windows 原生开始窗口。";
        public Dispatcher Dispatcher => _owner.Dispatcher;

        public Request(Window owner, Rect dock)
        {
            _owner = owner;
            _dock = dock;
            _eventCallback = OnWinEvent;
            _timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(32) };
            _timer.Tick += OnTick;
            _ownerClosed = (_, _) => Finish("cancelled", null, null);
        }

        public void Start()
        {
            RefreshProcesses();
            ReadWindows();
            _closing = _windows.Count != 0;
            _taskbarSuppression=StartTaskbarSuppression.Begin(Program.Lease);
            _deadline = Environment.TickCount64 + ObservationMilliseconds;
            _settleAt = Environment.TickCount64 + 400;
            _owner.Closed += _ownerClosed;
            // OUTOFCONTEXT queues callbacks in this process; no DLL or code enters Start.
            _showHook = SetWinEventHook(0x8002, 0x8003, 0, _eventCallback, 0, 0, 2);
            _locationHook = SetWinEventHook(0x800B, 0x800B, 0, _eventCallback, 0, 0, 2);
            Publish("observing", null, null);
            _timer.Start();
        }

        public void ToggleClosed()
        {
            _closing=true;
            _deadline=Environment.TickCount64+ObservationMilliseconds;
            _timer.Interval=TimeSpan.FromMilliseconds(32);
            ShellService.Start();
        }

        private void RefreshProcesses()
        {
            _startProcesses.Clear();
            using var ownProcess = Process.GetCurrentProcess();
            int ownSession = ownProcess.SessionId;
            foreach (var process in Process.GetProcessesByName("StartMenuExperienceHost"))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId == ownSession
                            && IsSystemStartProcess((uint)process.Id))
                            _startProcesses.Add((uint)process.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            _refreshProcessesAt = Environment.TickCount64 + 200;
        }

        private void ReadWindows()
        {
            _windows.Clear();
            foreach(var window in NativeStartWindows.Enumerate(_startProcesses))
            {
                GetWindowThreadProcessId(window, out uint processId);
                if (!_startProcesses.Contains(processId) || !IsWindowVisible(window)) continue;
                if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0) continue;
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() == "Windows.UI.Core.CoreWindow") _windows.Add(window);
                else _unsupportedReason = "Windows 开始窗口类型尚未支持：" + className;
            }
        }

        private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (_disposed || window == 0 || objectId != 0 || childId != 0 || _tickQueued) return;
            GetWindowThreadProcessId(window, out uint processId);
            if (!_startProcesses.Contains(processId) || Dispatcher.HasShutdownStarted) return;
            _tickQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _tickQueued = false;
                if (!_disposed) SafeTick();
            }), DispatcherPriority.Input);
        }

        private void OnTick(object? sender, EventArgs args) => SafeTick();

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { Finish("failed", false, ex.Message); }
        }

        private void Tick()
        {
            if (_disposed) return;
            long now = Environment.TickCount64;
            if (now >= _refreshProcessesAt) RefreshProcesses();
            ReadWindows();
            if (_closing)
            {
                if (_windows.Count == 0) Finish("closed", null, null);
                else if (now >= _deadline) Finish("toggle-unconfirmed", null, "Windows 尚未确认开始菜单已关闭。");
                return;
            }
            if (_windows.Count == 0)
            {
                if (_seenWindow) Finish(_verified ? "dismissed" : "dismissed-before-placement", _verified ? true : null, null);
                else if (now >= _deadline) Finish("unsupported", false, _unsupportedReason);
                return;
            }
            // One Start invocation must identify exactly one visible native root.
            if (_windows.Count != 1)
            {
                if (now >= _deadline) Finish("unsupported", false, "存在多个可见的原生开始窗口，未猜测定位对象。");
                return;
            }
            nint window = _windows[0];
            _seenWindow = true;
            if(_placementComplete) return;
            if (!TryGetPlacement(window, out Rect nativeBounds, out Rect desired, out string? reason))
            {
                _unsupportedReason = reason ?? _unsupportedReason;
                _window = window;
                _actual = nativeBounds.IsEmpty ? null : nativeBounds;
                Publish("observing", null, _unsupportedReason);
                if (now >= _deadline) PlacementCompleted("unsupported", false, _unsupportedReason);
                return;
            }
            if (_window != window || _original is null)
            {
                _window = window;
                _original = nativeBounds;
                _attempts = _confirmedReads = 0;
                _verified = false;
            }
            _actual = nativeBounds;
            _requested = desired;
            if (Near(nativeBounds, desired))
            {
                _verified = true;
                _confirmedReads++;
                Publish("verified", true, null);
                if ((_confirmedReads >= 2 && now >= _settleAt) || now >= _deadline) PlacementCompleted("positioned", true, null);
                return;
            }
            _confirmedReads = 0;
            if (now >= _deadline || (_attempts >= 3 && now - _lastMoveAt >= 160))
            {
                PlacementCompleted("unsupported", false, "Windows 未保留请求的开始菜单位置；原生菜单仍可使用。");
                return;
            }
            if (_attempts >= 3 || now - _lastMoveAt < 80) return;
            // Revalidate the owner before the only mutating call. Keep size, z-order,
            // activation and parent unchanged so all interaction remains native.
            GetWindowThreadProcessId(window, out uint processId);
            if (!_startProcesses.Contains(processId) || !IsSystemStartProcess(processId))
            {
                Finish("unsupported", false, "Windows 开始窗口已发生变化。");
                return;
            }
            _attempts++;
            _lastMoveAt = now;
            _settleAt = now + 400;
            bool accepted = SetWindowPos(window, 0, (int)Math.Round(desired.Left), (int)Math.Round(desired.Top), 0, 0,
                0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000);
            if (!accepted)
            {
                PlacementCompleted("unsupported", false, "Windows 拒绝移动原生开始窗口（" + Marshal.GetLastWin32Error() + "）。");
                return;
            }
            Publish("placement-requested", null, null);
        }

        private void PlacementCompleted(string status,bool? compatible,string? detail)
        {
            if(compatible==false) RestoreGeometry();
            _placementComplete=true;
            _timer.Interval=TimeSpan.FromMilliseconds(120);
            Publish(status,compatible,detail);
            SaveReport();
        }

        private bool TryGetPlacement(nint window, out Rect nativeBounds, out Rect desired, out string? reason)
        {
            nativeBounds = desired = Rect.Empty;
            reason = null;
            if (!GetWindowRect(window, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            { reason = "无法读取原生开始窗口范围。"; return false; }
            nativeBounds = bounds.ToRect();
            var sourceMonitor = MonitorInfo.Create();
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref sourceMonitor))
            { reason = "无法读取开始窗口所在显示器。"; return false; }
            var source = sourceMonitor.Monitor.ToRect();
            bool fullHost=nativeBounds.Width >= source.Width * .90 && nativeBounds.Height >= source.Height * .90;

            var dockBounds = NativeRect.FromRect(_dock);
            var targetMonitor = MonitorInfo.Create();
            nint target = MonitorFromRect(ref dockBounds, 2);
            if (!GetMonitorInfo(target, ref targetMonitor))
            { reason = "无法读取 Dock 所在显示器。"; return false; }
            // A cross-monitor HWND move can trigger a different-DPI relayout inside
            // Start. Let Windows choose the display rather than forcing that change.
            if (target != MonitorFromWindow(window, 2))
            { reason = "Windows 在其他显示器打开了开始菜单，未强制移动跨屏原生宿主。"; return false; }
            var work = targetMonitor.Work.ToRect();
            Rect visualBounds = nativeBounds;
            if(fullHost)
            {
                if(!TryReadContent(window,out visualBounds))
                { reason="原生开始面板尚未提供完整可见的交互范围。"; return false; }
                _originalContent??=visualBounds;
                _actualContent=visualBounds;
            }
            else if (DwmGetWindowAttribute(window, 8, out NativeRect frame, Marshal.SizeOf<NativeRect>()) == 0
                && frame.Right > frame.Left && frame.Bottom > frame.Top
                && frame.Left >= bounds.Left - 32 && frame.Top >= bounds.Top - 32
                && frame.Right <= bounds.Right + 32 && frame.Bottom <= bounds.Bottom + 32)
                visualBounds = frame.ToRect();
            double margin = fullHost?24:8;
            double availableHeight = _dock.Top - margin - (work.Top + margin);
            if (visualBounds.Width > work.Width - margin * 2 || visualBounds.Height > availableHeight)
            { reason = "Dock 上方不足以容纳原生大小的开始菜单。"; return false; }
            double x = Math.Clamp(_dock.Left + (_dock.Width - visualBounds.Width) / 2,
                work.Left + margin, work.Right - margin - visualBounds.Width) - (visualBounds.Left - nativeBounds.Left);
            double y = Math.Max(work.Top + margin, _dock.Top - margin - visualBounds.Height) - (visualBounds.Top - nativeBounds.Top);
            desired = new Rect(Math.Round(x), Math.Round(y), nativeBounds.Width, nativeBounds.Height);
            if(fullHost)
                _requestedContent=new Rect(visualBounds.Left+desired.Left-nativeBounds.Left,
                    visualBounds.Top+desired.Top-nativeBounds.Top,visualBounds.Width,visualBounds.Height);
            return true;
        }

        private static bool TryReadContent(nint window,out Rect content)
        {
            content=Rect.Empty;
            try
            {
                var root=AutomationElement.FromHandle(window);
                // These are actual interactive native elements, not guessed XAML type names.
                foreach(string id in new[]{"SearchBoxToggleButton","TopLevelRoot","UserControl"})
                {
                    var element=root.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.AutomationIdProperty,id));
                    if(element is null || element.Current.IsOffscreen) return false;
                    var rectangle=element.Current.BoundingRectangle;
                    if(rectangle.IsEmpty || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y)
                        || rectangle.Width<1 || rectangle.Height<1) return false;
                    content.Union(rectangle);
                }
                return content.Width>200 && content.Height>200;
            }
            catch(Exception exception) when(exception is ElementNotAvailableException or COMException or InvalidOperationException)
            { return false; }
        }

        private static bool Near(Rect actual, Rect requested) => Math.Abs(actual.Left - requested.Left) <= 2
            && Math.Abs(actual.Top - requested.Top) <= 2 && Math.Abs(actual.Width - requested.Width) <= 2
            && Math.Abs(actual.Height - requested.Height) <= 2;

        private void Publish(string status, bool? compatible, string? detail)
        {
            _report = new PlacementReport
            {
                Active = !_disposed, Status = status, Compatible = compatible, Detail = detail,
                Window = _window.ToInt64(), Attempts = _attempts, DockSurface = _dock,
                EventHooks = (_showHook != 0 ? 1 : 0) + (_locationHook != 0 ? 1 : 0),
                OriginalBounds = _original, RequestedBounds = _requested, ActualBounds = _actual
                ,OriginalContent=_originalContent,RequestedContent=_requestedContent,ActualContent=_actualContent
            };
        }

        public void Finish(string status, bool? compatible, string? detail)
        {
            if (_disposed) return;
            Dispose();
            Publish(status, compatible, detail);
            if (ReferenceEquals(_request, this)) _request = null;
            SaveReport();
            if (detail is not null) Program.Log("Native Start placement: " + detail);
        }

        private static void SaveReport()
        {
            try
            {
                Directory.CreateDirectory(Preferences.DataDirectory);
                File.WriteAllText(Path.Combine(Preferences.DataDirectory,"start-menu-placement.json"),
                    System.Text.Json.JsonSerializer.Serialize(_report,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
            }
            catch(Exception exception) { Program.Log("Start placement report: "+exception.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _owner.Closed -= _ownerClosed;
            if (_showHook != 0) UnhookWinEvent(_showHook);
            if (_locationHook != 0) UnhookWinEvent(_locationHook);
            _showHook = _locationHook = 0;
            RestoreGeometry();
            _taskbarSuppression?.Dispose();
            _taskbarSuppression=null;
            GC.KeepAlive(_eventCallback);
        }

        private void RestoreGeometry()
        {
            if(_window!=0 && _original is Rect original && _attempts>0)
            {
                GetWindowThreadProcessId(_window,out uint processId);
                if(_startProcesses.Contains(processId) && IsSystemStartProcess(processId))
                    SetWindowPos(_window,0,(int)Math.Round(original.Left),(int)Math.Round(original.Top),0,0,
                        0x0001|0x0004|0x0010|0x0200|0x4000);
            }
        }
    }

    private static bool IsSystemStartProcess(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId);
        if (process == 0) return false;
        try
        {
            var path = new StringBuilder(1024);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) return false;
            string actual = Path.GetFullPath(path.ToString());
            string systemApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps")
                + Path.DirectorySeparatorChar;
            return actual.StartsWith(systemApps + "Microsoft.Windows.StartMenuExperienceHost_", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(actual), "StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(actual)), systemApps.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        public static NativeRect FromRect(Rect value) => new()
        { Left = (int)Math.Floor(value.Left), Top = (int)Math.Floor(value.Top), Right = (int)Math.Ceiling(value.Right), Bottom = (int)Math.Ceiling(value.Bottom) };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
    }
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect bounds, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
