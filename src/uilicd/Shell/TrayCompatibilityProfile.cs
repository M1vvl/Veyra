using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace GlassDock.Shell;

public sealed record TrayBinaryFingerprint(string Path, string Version, string Sha256, long Bytes, string? Error);

public sealed record TrayCompatibilityReport(
    string ProfileName,
    string OsBuild,
    string ExplorerVersion,
    string TaskbarVersion,
    string SystemTrayVersion,
    bool NativeTrayFallbackRequired,
    string[] Limitations)
{
    public bool MatchesObservedStaticProfile { get; init; }
    public bool RuntimeDelegationValidated { get; init; }
    public IReadOnlyList<TrayBinaryFingerprint> Binaries { get; init; } = [];
    /// <summary>Names observed in PE strings. They are not validated UIA AutomationIds.</summary>
    public string[] StaticTypeNameHints { get; init; } = [];
    public string[] WindowNameOrClassHints { get; init; } = [];
}

/// <summary>
/// Development-time REA evidence informs these fingerprints. Capture itself uses only
/// read-only OS/file APIs: no REA process, Explorer COM callback, injection or UI probing.
/// Call on explicit diagnostics, not on a render/status timer.
/// </summary>
public static class TrayCompatibilityProfile
{
    private const string ObservedBuild = "10.0.26200.9711";
    private static readonly string[] ObservedHashes =
    [
        "eee089fe6f927c7f0d51b0abd9d70c094157e33673ad760713ec7b1cea18ee11",
        "ca1e335ac1fae5ff57519035302a2f365c4620d3a230f31f1231af7331760741",
        "c5f5260a2a56c4d7b0ee25978aa06c387b5ddffc57c5fe412be429d1289913ce",
        "cf7e002533def6628ba121481a703a06fc30accaa5e5cadd1a412f65f2d8ba77"
    ];

    public static TrayCompatibilityReport Capture()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var paths = new[]
        {
            System.IO.Path.Combine(windows, "explorer.exe"),
            System.IO.Path.Combine(Environment.SystemDirectory, "Taskbar.dll"),
            System.IO.Path.Combine(windows, "SystemApps", "MicrosoftWindows.Client.Core_cw5n1h2txyewy", "SystemTray.dll"),
            System.IO.Path.Combine(windows, "SystemApps", "MicrosoftWindows.Client.Core_cw5n1h2txyewy", "Taskbar.View.dll")
        };
        var files = paths.Select(Fingerprint).ToArray();
        var build = ReadOsBuild();
        var matched = build == ObservedBuild && files.Select((file, i) =>
            file.Error is null && string.Equals(file.Sha256, ObservedHashes[i], StringComparison.OrdinalIgnoreCase)).All(value => value);
        var limitations = new List<string>
        {
            "静态指纹匹配只表示与2026-11-09检查的本机文件相同，不表示托盘完整迁移或点击代理已经验证。",
            "PE中的SystemTray类型名仅作为候选查找线索；未验证它们等同于当前UIA ClassName或AutomationId。",
            "ITrayNotifyWin8仅允许在独立辅助进程中进行受限快照探测；NOTIFYITEM不包含应用的uCallbackMessage和uVersion，不能据此猜测原生点击消息。",
            "此版本始终保留Explorer原生托盘入口；不注入Explorer，不读取或修改其私有内存。"
        };
        if (!matched) limitations.Add("当前系统或组件指纹未匹配已观察的静态配置，必须继续使用原生托盘回退。");
        limitations.AddRange(files.Where(file => file.Error is not null)
            .Select(file => $"无法读取组件：{file.Path}：{file.Error}"));
        return new TrayCompatibilityReport(
            matched ? "windows-25h2-26200.9711-rea-static-observed" : "unverified-windows-shell",
            build, files[0].Version, files[1].Version, files[2].Version, true, limitations.ToArray())
        {
            MatchesObservedStaticProfile = matched,
            RuntimeDelegationValidated = false,
            Binaries = files,
            StaticTypeNameHints =
            [
                "SystemTray.NotifyIconView", "SystemTray.ChevronIconView",
                "SystemTray.NotificationAreaOverflow", "SystemTray.SystemTrayFrame",
                "SystemTray.SystemTrayButtonAutomationPeer"
            ],
            WindowNameOrClassHints = ["TrayNotifyWnd", "TopLevelWindowForOverflowXamlIsland"]
        };
    }

    private static TrayBinaryFingerprint Fingerprint(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = stream.Length;
            if (bytes > 64L * 1024 * 1024) return new(path, version, "", bytes, "组件超过诊断读取上限64MiB。");
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new(path, version, hash, bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(path, "unknown", "", 0, ex.Message);
        }
    }

    private static string ReadOsBuild()
    {
        var version = new OsVersionInfo { Size = (uint)Marshal.SizeOf<OsVersionInfo>(), ServicePack = "" };
        try
        {
            if (RtlGetVersion(ref version) != 0) return "unknown";
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            var revision = key?.GetValue("UBR");
            var basic = $"{version.Major}.{version.Minor}.{version.Build}";
            return revision is int ubr ? $"{basic}.{ubr}" : basic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref OsVersionInfo info);
}
