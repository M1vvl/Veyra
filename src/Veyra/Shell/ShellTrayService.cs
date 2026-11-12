using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GlassDock.Shell;

public enum NativeTrayClick { Primary, Secondary, Double }
public sealed record NativeTrayItem(string Id, string Name, bool IsSystemModule);

/// <summary>
/// On-demand accessibility bridge into Explorer's real notification area. It deliberately does
/// not inject into Explorer, read another process' private NOTIFYICONDATA, or impersonate a tray.
/// Native controls remain the source of truth, including application context menus.
/// </summary>
public sealed class ShellTrayService
{
    private readonly TaskbarLease _lease;
    private readonly Dictionary<string, AutomationElement> _elements = new();
    private readonly object _elementsLock = new();
    private int _busy;
    public ShellTrayService(TaskbarLease lease) => _lease = lease;

    public void RevealNativeTray() => _lease.RevealNativeTaskbar(TimeSpan.FromSeconds(20));

    /// <summary>Shows the native hidden-icons popup through its actual accessibility control.</summary>
    public async Task<bool> ShowHiddenIconsAsync()
    {
        var items = await GetItemsAsync();
        var overflow = items.FirstOrDefault(x => x.Id.Contains("Overflow", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains("隐藏", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("hidden", StringComparison.OrdinalIgnoreCase));
        if (overflow is not null) return await InvokeAsync(overflow.Id);
        RevealNativeTray();
        // Keyboard focus lands on the real notification area. The user can use its native chevron.
        ShellService.Shortcut(0x5B, 0x42);
        return false;
    }

    /// <summary>
    /// Enumerates only when requested. A timed-out provider leaves its one background worker
    /// alive, rather than spawning an unbounded series of blocked UIAutomation threads.
    /// </summary>
    public Task<IReadOnlyList<NativeTrayItem>> GetItemsAsync() => RunBounded(() =>
    {
        RevealNativeTray();
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return (IReadOnlyList<NativeTrayItem>)Array.Empty<NativeTrayItem>();
        var root = AutomationElement.FromHandle(tray);
        var controls = root.FindAll(TreeScope.Descendants,
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
        var list = new List<NativeTrayItem>();
        var elements = new Dictionary<string, AutomationElement>();
        for (int i = 0; i < controls.Count && list.Count < 48; ++i)
        {
            var element = controls[i];
            try
            {
                var current = element.Current;
                if (string.IsNullOrWhiteSpace(current.Name) || !IsTrayDescendant(element, root)) continue;
                string automationId = current.AutomationId;
                string id = automationId + ":" + string.Join(".", element.GetRuntimeId());
                if (elements.ContainsKey(id)) continue;
                bool system = !string.IsNullOrEmpty(automationId) && !automationId.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase);
                elements[id] = element;
                list.Add(new NativeTrayItem(id, current.Name.Trim(), system));
            }
            catch (ElementNotAvailableException) { }
        }
        // Overflow is a separate top-level Explorer island on Windows 11.
        foreach (string className in new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
        {
            var overflow = FindWindow(className, null);
            if (overflow == IntPtr.Zero || !IsWindowVisible(overflow)) continue;
            var popup = AutomationElement.FromHandle(overflow);
            var buttons = popup.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            for (int i = 0; i < buttons.Count && list.Count < 64; ++i)
            {
                var element = buttons[i];
                try
                {
                    if (string.IsNullOrWhiteSpace(element.Current.Name)) continue;
                    string id = "OverflowItem:" + string.Join(".", element.GetRuntimeId());
                    if (elements.ContainsKey(id)) continue;
                    elements[id] = element;
                    list.Add(new NativeTrayItem(id, element.Current.Name.Trim(), false));
                }
                catch (ElementNotAvailableException) { }
            }
        }
        lock (_elementsLock) { _elements.Clear(); foreach (var pair in elements) _elements[pair.Key] = pair.Value; }
        return (IReadOnlyList<NativeTrayItem>)list;
    }, (IReadOnlyList<NativeTrayItem>)Array.Empty<NativeTrayItem>());

    public Task<bool> InvokeAsync(string id, NativeTrayClick click = NativeTrayClick.Primary) => RunBounded(() =>
    {
        AutomationElement? element;
        lock (_elementsLock) _elements.TryGetValue(id, out element);
        if (element is null) { RevealNativeTray(); return false; }
        RevealNativeTray();
        if (click == NativeTrayClick.Primary && element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            ((InvokePattern)pattern).Invoke();
            return true;
        }
        // Context menus / double-click belong to the application and must receive actual native
        // tray mouse events. Coordinates are fetched immediately, never trusted from a stale list.
        var bounds = element.Current.BoundingRectangle;
        if (element.Current.IsOffscreen || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
        if (!GetCursorPos(out var old)) return false;
        int x = (int)(bounds.Left + bounds.Width / 2), y = (int)(bounds.Top + bounds.Height / 2);
        if (!SetCursorPos(x, y)) return false;
        try
        {
            uint down = click == NativeTrayClick.Secondary ? 0x0008u : 0x0002u;
            uint up = click == NativeTrayClick.Secondary ? 0x0010u : 0x0004u;
            var inputs = new List<Input> { Mouse(down), Mouse(up) };
            if (click == NativeTrayClick.Double) { inputs.Add(Mouse(down)); inputs.Add(Mouse(up)); }
            return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) == inputs.Count;
        }
        finally { SetCursorPos(old.X, old.Y); }
    }, false);

    private static bool IsTrayDescendant(AutomationElement element, AutomationElement root)
    {
        var current = element;
        for (int depth = 0; depth < 16 && current is not null && !current.Equals(root); ++depth)
        {
            var info = current.Current;
            if (info.ClassName == "TrayNotifyWnd" || info.ClassName.StartsWith("SystemTray.", StringComparison.Ordinal) ||
                info.AutomationId.StartsWith("SystemTray", StringComparison.Ordinal)) return true;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private async Task<T> RunBounded<T>(Func<T> action, T fallback)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return fallback;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T result = fallback;
            try { result = action(); }
            catch (Exception ex) { Trace.WriteLine("Native tray: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
            completion.TrySetResult(result);
        }) { IsBackground = true, Name = "GlassDock native tray" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return await Task.WhenAny(completion.Task, Task.Delay(2500)) == completion.Task ? await completion.Task : fallback;
    }

    private static Input Mouse(uint flags) => new() { Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
