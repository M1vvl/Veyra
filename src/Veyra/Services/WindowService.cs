using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GlassDock.Models;

namespace GlassDock.Services;

/// <summary>Read-only window discovery plus explicit user-requested activation/launch.</summary>
public static class WindowService
{
    public static List<WindowSnapshot> GetAllWindows()
    {
        var windows = new List<WindowSnapshot>();
        var processes = new Dictionary<uint, (string Path, string AppId)>();
        var ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            var style = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((style & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != nint.Zero && (style & 0x40000 /* WS_EX_APPWINDOW */) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownPid || processId == 0) return true;
            var className = new StringBuilder(128);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return true;
            var title = new StringBuilder(Math.Min(GetWindowTextLength(hwnd) + 1, 32768));
            GetWindowText(hwnd, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;
            if (!processes.TryGetValue(processId, out var process))
            {
                process = GetProcessDetails(processId);
                processes[processId] = process;
            }
            var appId = GetWindowAppId(hwnd);
            windows.Add(new WindowSnapshot(hwnd, processId, title.ToString(), process.Path,
                string.IsNullOrWhiteSpace(appId) ? process.AppId : appId));
            return true;
        }, nint.Zero);
        return windows;
    }

    private static readonly ConcurrentDictionary<string, bool> DirectoryTargets = new(StringComparer.OrdinalIgnoreCase);
    internal static void RefreshDirectoryTargets(IReadOnlyList<PinnedApp> pins)
    {
        var targets = pins.Select(app => app.Target.Length > 0 ? app.Target : app.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string target in targets) DirectoryTargets[target] = Directory.Exists(target);
        foreach (string stale in DirectoryTargets.Keys) if (!targets.Contains(stale)) DirectoryTargets.TryRemove(stale, out _);
    }

    public static List<WindowSnapshot> GetWindows(PinnedApp app, IReadOnlyList<WindowSnapshot>? snapshot = null)
    {
        snapshot ??= GetAllWindows();
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
        {
            var identityMatches = snapshot.Where(w => IsWindow(w.Handle) && w.AppUserModelId.Equals(app.AppUserModelId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (identityMatches.Count > 0) return identityMatches;
        }
        var target = app.Target.Length > 0 ? app.Target : app.Path;
        if (DirectoryTargets.TryGetValue(target, out bool directory) && directory) target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!System.IO.Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return [];
        return snapshot.Where(w => IsWindow(w.Handle) && w.ExecutablePath.Equals(target, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(app.AppUserModelId) || string.IsNullOrWhiteSpace(w.AppUserModelId)
                || w.AppUserModelId.Equals(app.AppUserModelId,StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static List<PinnedApp> GetRunningApps(IReadOnlyList<WindowSnapshot>? snapshot = null)
    {
        snapshot ??= GetAllWindows();
        return snapshot.Where(w => !string.IsNullOrWhiteSpace(w.ExecutablePath))
            .GroupBy(w => string.IsNullOrWhiteSpace(w.AppUserModelId) ? w.ExecutablePath : w.AppUserModelId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var window = group.First();
                var executable = window.ExecutablePath;
                // The window title distinguishes Windows Store apps sharing a host executable.
                var name = FriendlyName(executable);
                if (System.IO.Path.GetFileName(executable).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) name = window.Title;
                return new PinnedApp
                {
                    Id = "running:" + group.Key.ToUpperInvariant(),
                    Name = name,
                    Path = executable,
                    Target = executable,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? "",
                    AppUserModelId = window.AppUserModelId
                };
            }).ToList();
    }

    public static bool Activate(WindowSnapshot window) => Activate(window.Handle);

    public static bool Activate(nint hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9 /* SW_RESTORE */);
        var success = SetForegroundWindow(hwnd);
        if (!success)
        {
            // Respect Windows foreground-stealing protection. Draw attention rather than
            // attaching foreign input threads or simulating clicks in another application.
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 3, Count = 2 };
            FlashWindowEx(ref flash);
        }
        return success;
    }

    public static void ActivateOrLaunch(PinnedApp app, IReadOnlyList<WindowSnapshot>? snapshot = null)
    {
        var windows = GetWindows(app, snapshot);
        if (windows.Count > 0)
        {
            var foreground = GetForegroundWindow();
            var currentIndex = windows.FindIndex(w => w.Handle == foreground);
            // Repeated clicks cycle the application's windows instead of starting duplicates.
            Activate(windows[(currentIndex + 1) % windows.Count]);
            return;
        }
        Launch(app);
    }

    public static void Launch(PinnedApp app)
    {
        var originalExists = File.Exists(app.Path) || Directory.Exists(app.Path);
        var extension = System.IO.Path.GetExtension(app.Path);
        var originalIsShortcut = originalExists && (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase));
        ProcessStartInfo start;
        if (originalIsShortcut)
        {
            // Let Windows honor run-as, advertised MSI links, shell targets and URL handlers.
            // Supplying the extracted arguments again would duplicate the shortcut arguments.
            start = new ProcessStartInfo(app.Path) { UseShellExecute = true };
        }
        else if (!string.IsNullOrWhiteSpace(app.AppUserModelId) && RequiresAppFolderActivation(app))
        {
            start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                "shell:AppsFolder\\" + app.AppUserModelId) { UseShellExecute = true };
        }
        else
        {
            var target = !string.IsNullOrWhiteSpace(app.Target) ? app.Target : app.Path;
            start = new ProcessStartInfo(target, app.Arguments) { UseShellExecute = true };
            if (Directory.Exists(app.WorkingDirectory)) start.WorkingDirectory = app.WorkingDirectory;
        }
        Process.Start(start)?.Dispose();
    }

    private static bool RequiresAppFolderActivation(PinnedApp app) =>
        app.Target.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(app.Target).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(app.Target);

    private static (string Path, string AppId) GetProcessDetails(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == nint.Zero) return ("", "");
        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            var image = QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : "";
            uint appLength = 0;
            var appId = "";
            if (GetApplicationUserModelId(process, ref appLength, null) == 122 && appLength <= 32768)
            {
                var buffer = new StringBuilder((int)appLength);
                if (GetApplicationUserModelId(process, ref appLength, buffer) == 0) appId = buffer.ToString();
            }
            return (image, appId);
        }
        finally { CloseHandle(process); }
    }

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    private static string FriendlyName(string executable)
    {
        if (NameCache.TryGetValue(executable, out var name)) return name;
        name = System.IO.Path.GetFileNameWithoutExtension(executable);
        try
        {
            var description = FileVersionInfo.GetVersionInfo(executable).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (NameCache.Count > 200) NameCache.Clear();
        return NameCache[executable] = name;
    }

    private static string GetWindowAppId(nint hwnd)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0 || store is null) return "";
            var key = new PropertyKey { FormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            if (store.GetValue(ref key, out value) < 0 || value.Type != 31 || value.Pointer == nint.Zero) return "";
            return Marshal.PtrToStringUni(value.Pointer) ?? "";
        }
        catch (COMException) { return ""; }
        finally
        {
            PropVariantClear(ref value);
            if (store is not null) Marshal.FinalReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo
    {
        public uint Size; public nint Window; public uint Flags; public uint Count; public uint Timeout;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder title, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? appId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
