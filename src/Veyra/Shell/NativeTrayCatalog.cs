using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDock.Shell;

public sealed record NativeTrayIcon(string Key, string OwnerPath, string Tooltip, long WindowHandle,
    uint IconId, string IconGuid, uint Preference, string PngBase64);

public sealed record NativeTrayProbeEvent(uint EventCode, long WindowHandle, long IconHandle, uint IconId,
    string IconGuid, uint Preference);

public sealed record NativeTrayProbeCounters(int CallbackNotifications, int LiveWindowCandidates,
    int HistoricalItems, int InvalidWindows, int MissingIcons, int CopyIconFailures,
    IReadOnlyList<NativeTrayProbeEvent> FirstEvents);

public sealed record NativeTraySnapshot(string Status, string ProfileName, string OsBuild,
    IReadOnlyList<NativeTrayIcon> Icons, string[] Limitations, string? Error)
{
    public bool EnumerationSucceeded { get; init; }
    public bool NativeTrayFallbackRequired { get; init; } = true;
    public bool ActionDelegationValidated { get; init; }
    public bool Truncated { get; init; }
    public NativeTrayProbeCounters Diagnostics { get; init; } = new(0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// Exact-fingerprint, helper-process-only snapshot of Explorer's original tray icons.
/// The private interface declarations follow Chromium's primary implementation.
/// This class never changes preferences and never guesses an application's callback message.
/// </summary>
public static class NativeTrayCatalog
{
    private static readonly Guid TrayNotifyClass = new("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD");
    private const int MaxIcons = 256;
    private const int MaxResponseCharacters = 8 * 1024 * 1024;

    /// <summary>Run only from Program's --tray-snapshot branch, before creating any UI.</summary>
    public static NativeTraySnapshot CaptureSnapshot()
    {
        if (!Environment.GetCommandLineArgs().Contains("--tray-snapshot"))
            return Failure("helper-required", "私有托盘枚举只允许在独立的 --tray-snapshot 辅助进程中运行。");
        var profile = TrayCompatibilityProfile.Capture();
        NativeTraySnapshot Fail(string status, string error) => Failure(status, error) with
        { ProfileName = profile.ProfileName, OsBuild = profile.OsBuild, Limitations = profile.Limitations };
        if (!profile.MatchesObservedStaticProfile)
            return Fail("unsupported-profile", "系统或组件指纹未匹配已观察的26200.9711静态配置；没有调用私有COM接口。");
        if (!Environment.Is64BitProcess || Marshal.SizeOf<NotifyItem>() != 56 || Marshal.OffsetOf<NotifyItem>(nameof(NotifyItem.Window)).ToInt32() != 24)
            return Fail("unsupported-layout", "当前进程不符合经源码核对的x64 NOTIFYITEM布局。");

        var initializeResult = CoInitializeEx(nint.Zero, 2 /* COINIT_APARTMENTTHREADED */);
        if (initializeResult < 0) return Fail("com-apartment-unavailable", $"无法建立STA COM环境：0x{initializeResult:X8}");
        try { return CaptureInitialized(profile); }
        finally { CoUninitialize(); }
    }

    private static NativeTraySnapshot CaptureInitialized(TrayCompatibilityReport profile)
    {
        NativeTraySnapshot Fail(string status, string error) => Failure(status, error) with
        { ProfileName = profile.ProfileName, OsBuild = profile.OsBuild, Limitations = profile.Limitations };

        ITrayNotifyWin8? tray = null;
        var callback = new NotificationCallback();
        uint cookie = 0; // Windows ULONG is 32 bits on both Win32 and Win64.
        bool registered = false;
        int unregisterResult = 0;
        try
        {
            var clsid = TrayNotifyClass;
            var iid = typeof(ITrayNotifyWin8).GUID;
            // Match Chromium's activation context. All code still runs in this disposable helper.
            var result = CoCreateInstance(ref clsid, nint.Zero, 23 /* CLSCTX_ALL */, ref iid, out tray);
            if (result < 0 || tray is null) return Fail("interface-unavailable", $"ITrayNotifyWin8不可用：0x{result:X8}");
            result = tray.RegisterCallback(callback, out cookie);
            registered = result >= 0;
            if (!registered) return Fail("registration-failed", $"托盘只读快照回调失败：0x{result:X8}");
        }
        catch (COMException ex) { return Fail("com-error", ex.Message); }
        finally
        {
            // Explorer calls back synchronously for existing entries. End the subscription
            // immediately, before bitmap conversion or serialization can take additional time.
            if (registered && tray is not null)
            {
                try { unregisterResult = tray.UnregisterCallback(ref cookie); }
                catch (COMException ex) { unregisterResult = ex.HResult; }
            }
            if (tray is not null) Marshal.FinalReleaseComObject(tray);
            GC.KeepAlive(callback);
            if (!registered) callback.Dispose();
        }

        try
        {
            var icons = new List<NativeTrayIcon>();
            int encodedCharacters = 0;
            foreach (var item in callback.Items.Values)
            {
                var png = EncodeIcon(item.Icon);
                encodedCharacters += png.Length;
                if (encodedCharacters > MaxResponseCharacters - 512 * 1024)
                {
                    callback.Truncated = true;
                    break;
                }
                icons.Add(new NativeTrayIcon(item.Key, item.OwnerPath, item.Tooltip, item.Window.ToInt64(),
                    item.Id, item.Guid.ToString("D"), item.Preference, png));
            }
            var limitations = new List<string>
            {
                "仅枚举当前活动的第三方通知图标；系统输入法、网络、音量和隐藏区仍保留Explorer原生入口。",
                "快照提供原始图标与提示文字，不含应用uCallbackMessage/uVersion；没有启用猜测消息的点击代理。",
                "REA用于开发期静态核对；此快照辅助进程不启动REA或反编译引擎。"
            };
            if (icons.Count == 0) limitations.Add("本次接口调用成功但没有取得活动图标；不能据此宣称当前Shell的原始托盘枚举已适配。");
            if (callback.Truncated) limitations.Add("活动图标数量或序列化大小超过快照上限，结果已明确标记为不完整。");
            if (callback.LastError is { } error) limitations.Add("部分条目读取失败：" + error);
            if (unregisterResult < 0) limitations.Add($"取消快照回调返回0x{unregisterResult:X8}；辅助进程随即退出，保留原生托盘回退。");
            return new NativeTraySnapshot(unregisterResult < 0 ? "cleanup-failed" : icons.Count > 0 ? "ok" : "empty",
                profile.ProfileName, profile.OsBuild, icons, limitations.ToArray(),
                unregisterResult < 0 ? $"UnregisterCallback: 0x{unregisterResult:X8}" : callback.LastError)
            {
                EnumerationSucceeded = unregisterResult >= 0,
                Truncated = callback.Truncated,
                Diagnostics = callback.GetDiagnostics()
            };
        }
        finally { callback.Dispose(); }
    }

    /// <summary>Bounded IPC. Failure of a private ABI stays in an owned helper process.</summary>
    public static async Task<NativeTraySnapshot> CaptureInHelperAsync(TimeSpan? timeout = null)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return Failure("helper-unavailable", "无法定位托盘快照辅助程序。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory,"Veyra.dll"));
        start.ArgumentList.Add("--tray-snapshot");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Failure("helper-start-failed", "托盘快照辅助进程未启动。");
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(4));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var diagnostic = await stderr.ConfigureAwait(false);
            if (text.Length > MaxResponseCharacters) return Failure("response-too-large", "托盘快照响应超过8MiB上限。");
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
                return Failure("helper-failed", $"托盘快照辅助进程退出({process.ExitCode})。" + diagnostic[..Math.Min(diagnostic.Length, 1000)]);
            return JsonSerializer.Deserialize<NativeTraySnapshot>(text) ?? Failure("empty-response", "托盘快照响应为空。");
        }
        catch (OperationCanceledException)
        {
            // This Process instance owns the helper handle. No Explorer process is stopped.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            return Failure("helper-timeout", "托盘快照辅助进程超时，已结束该辅助进程并保留原生托盘。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failure("helper-error", ex.Message);
        }
    }

    public static BitmapSource? DecodeIcon(NativeTrayIcon icon)
    {
        if (string.IsNullOrWhiteSpace(icon.PngBase64)) return null;
        using var stream = new MemoryStream(Convert.FromBase64String(icon.PngBase64), false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static NativeTraySnapshot Failure(string status, string error) => new(status, "unverified-windows-shell", "unknown", [],
        ["未获得可用的原生托盘快照，继续使用Explorer原生托盘入口。"], error);

    private static string EncodeIcon(nint icon)
    {
        if (icon == nint.Zero) return "";
        BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        if (bitmap.PixelWidth > 96 || bitmap.PixelHeight > 96)
        {
            var ratio = 96d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(ratio, ratio));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        return Convert.ToBase64String(output.ToArray());
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NotifyItem
    {
        public nint ExecutableName, Tooltip, Icon, Window;
        public uint Preference, Id;
        public Guid Guid;
    }
    internal sealed record CapturedItem(string Key, string OwnerPath, string Tooltip, nint Icon,
        nint Window, uint Preference, uint Id, Guid Guid);

    [ComVisible(true), Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationCallback
    {
        [PreserveSig] int Notify(uint eventCode, nint notifyItem);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class NotificationCallback : INotificationCallback, IDisposable
    {
        internal readonly Dictionary<string, CapturedItem> Items = [];
        public bool Truncated;
        public string? LastError;
        private int _callbackNotifications, _liveWindowCandidates, _historicalItems, _invalidWindows, _missingIcons, _copyIconFailures;
        private readonly List<NativeTrayProbeEvent> _firstEvents = [];

        public NativeTrayProbeCounters GetDiagnostics() => new(_callbackNotifications, _liveWindowCandidates,
            _historicalItems, _invalidWindows, _missingIcons, _copyIconFailures, _firstEvents.ToArray());

        public int Notify(uint eventCode, nint notifyItem)
        {
            try
            {
                if (notifyItem == nint.Zero) return 0;
                _callbackNotifications++;
                var item = Marshal.PtrToStructure<NotifyItem>(notifyItem);
                if (_firstEvents.Count < 16) _firstEvents.Add(new NativeTrayProbeEvent(eventCode, item.Window.ToInt64(),
                    item.Icon.ToInt64(), item.Id, item.Guid.ToString("D"), item.Preference));
                // Explorer also remembers historical icons. Those are not live modules.
                if (item.Window == nint.Zero) { _historicalItems++; return 0; }
                if (!IsWindow(item.Window)) { _invalidWindows++; return 0; }
                _liveWindowCandidates++;
                if (item.Icon == nint.Zero) { _missingIcons++; return 0; }
                var key = item.Guid != Guid.Empty ? item.Guid.ToString("D") : $"{item.Window.ToInt64():x}:{item.Id}";
                if (!Items.ContainsKey(key) && Items.Count >= MaxIcons) { Truncated = true; return 0; }
                var owner = ReadString(item.ExecutableName);
                var tooltip = ReadString(item.Tooltip);
                var copy = CopyIcon(item.Icon);
                if (copy == nint.Zero) { _copyIconFailures++; return 0; }
                if (Items.TryGetValue(key, out var previous)) DestroyIcon(previous.Icon);
                Items[key] = new CapturedItem(key, owner, tooltip, copy,
                    item.Window, item.Preference, item.Id, item.Guid);
            }
            catch (Exception ex) { LastError = ex.Message; }
            // Exceptions must never cross Explorer's callback boundary.
            return 0;
        }
        private static string ReadString(nint value)
        {
            var text = value == nint.Zero ? "" : Marshal.PtrToStringUni(value) ?? "";
            return text[..Math.Min(text.Length, 4096)];
        }
        public void Dispose()
        {
            foreach (var item in Items.Values) if (item.Icon != nint.Zero) DestroyIcon(item.Icon);
            Items.Clear();
        }
    }

    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITrayNotifyWin8
    {
        [PreserveSig] int RegisterCallback([MarshalAs(UnmanagedType.Interface)] INotificationCallback callback, out uint callbackId);
        [PreserveSig] int UnregisterCallback(ref uint callbackId);
        // Preference/mutation methods are deliberately absent from this read-only adapter.
    }
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out ITrayNotifyWin8? instance);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
}
