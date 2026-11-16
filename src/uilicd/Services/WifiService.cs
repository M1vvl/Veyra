using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassDock.Services;

public sealed record WifiInterface(
    Guid Id,
    string Description,
    bool? RadioEnabled,
    bool HardwareBlocked,
    string State);

public sealed record WifiNetwork(
    Guid InterfaceId,
    string Ssid,
    string ProfileName,
    int Signal,
    bool Secured,
    bool Connected,
    bool Connectable,
    string? UnavailableReason)
{
    public bool HasProfile => !string.IsNullOrWhiteSpace(ProfileName);
    public uint BssType { get; init; } = 1;
    public byte[] SsidBytes { get; init; } = [];
    public bool IsInRange { get; init; } = true;
    public bool SecurityKnown { get; init; } = true;
}

public sealed record WifiSnapshot(
    bool IsAvailable,
    bool? RadioEnabled,
    bool HardwareBlocked,
    string? ConnectedName,
    IReadOnlyList<WifiInterface> Interfaces,
    IReadOnlyList<WifiNetwork> Networks,
    string? Error)
{
    internal NetworkConnectionSnapshot Connections {get;init;}=new(false,null,[]);
    public bool ScanAccessDenied {get;init;}
}

public sealed record WifiOperationResult(bool Succeeded, string Message);

/// <summary>
/// Direct native WLAN control for the app's own panel. Native calls are serialized on one
/// background worker. A stalled WLAN provider cannot produce unbounded worker threads.
/// </summary>
public static class WifiService
{
    private const uint RadioStateOpcode = 4;
    private const uint CurrentConnectionOpcode = 7;
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(4);
    private static readonly object SnapshotSync=new();
    private static Task<WifiSnapshot>? _pendingSnapshot;
    private static Task<WifiSnapshot>? _queuedScan;
    private static bool _pendingIncludesScan;

    public static Task<WifiSnapshot> GetSnapshotAsync(
        bool scan = false,
        CancellationToken cancellationToken = default)
    {
        lock(SnapshotSync)
        {
            // Overview and Network often request the same native snapshot while
            // navigating. Reuse that read instead of reporting the WLAN gate as unavailable.
            if(_pendingSnapshot is { IsCompleted:false }) {
                if(scan && !_pendingIncludesScan)
                    return (_queuedScan??=ScanAfterReadAsync(_pendingSnapshot)).WaitAsync(cancellationToken);
                return _pendingSnapshot.WaitAsync(cancellationToken);
            }
            _queuedScan=null;
            _pendingIncludesScan=scan;
            _pendingSnapshot=RunBoundedAsync(()=>ReadSnapshot(scan),
                message=>new WifiSnapshot(false,null,false,null,[],[],message) {Connections=NetworkConnections.Read()},CancellationToken.None);
            return _pendingSnapshot.WaitAsync(cancellationToken);
        }
    }

    private static async Task<WifiSnapshot> ScanAfterReadAsync(Task<WifiSnapshot> pending)
    {
        await pending.ConfigureAwait(false);
        return await GetSnapshotAsync(true).ConfigureAwait(false);
    }

    public static Task<WifiOperationResult> SetRadioAsync(
        bool enabled,
        Guid? interfaceId = null,
        CancellationToken cancellationToken = default)
    {
        return RunBoundedAsync(
            () => SetRadio(enabled, interfaceId),
            message => new WifiOperationResult(false, message),
            cancellationToken);
    }

    public static Task<WifiOperationResult> ConnectAsync(
        WifiNetwork network,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        return RunBoundedAsync(
            () => Connect(network),
            message => new WifiOperationResult(false, message),
            cancellationToken);
    }

    public static Task<WifiOperationResult> DisconnectAsync(
        Guid interfaceId,
        CancellationToken cancellationToken = default)
    {
        return RunBoundedAsync(() =>
        {
            using var client = WlanClient.Open();
            Check(WlanDisconnect(client.Handle, ref interfaceId, nint.Zero), "断开 Wi-Fi");
            return new WifiOperationResult(true, "正在断开连接");
        }, message => new WifiOperationResult(false, message), cancellationToken);
    }

    private static async Task<T> RunBoundedAsync<T>(
        Func<T> operation,
        Func<string, T> failure,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await OperationGate.WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false))
                return failure("Wi-Fi 正在处理上一项操作，请稍后重试");
        }
        catch (OperationCanceledException)
        {
            return failure("操作已取消");
        }

        var worker = Task.Run(() =>
        {
            try { return operation(); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or DllNotFoundException)
            {
                return failure(exception.Message);
            }
            finally { OperationGate.Release(); }
        });

        try
        {
            return await worker.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The native operation retains its handle until it returns. A later read reconciles
            // state, so a timeout never falsely reports a network mutation as completed.
            return failure("Wi-Fi 驱动响应超时，操作结果尚未确认，请稍后刷新状态");
        }
        catch (OperationCanceledException)
        {
            return failure("已停止等待，Wi-Fi 操作结果请刷新确认");
        }
    }

    private static WifiSnapshot ReadSnapshot(bool scan)
    {
        ValidateLayouts();
        using var client = WlanClient.Open();
        var interfaces = new List<WifiInterface>();
        var networks = new List<WifiNetwork>();
        var errors = new List<string>();
        var connections=NetworkConnections.Read();
        bool accessDenied=false;
        string? connectedName = null;

        foreach (var nativeInterface in ReadInterfaces(client.Handle))
        {
            Guid id = nativeInterface.Id;
            var radios = ReadRadioStates(client.Handle, id, errors);
            bool? enabled = radios.Count == 0 ? null : radios.Any(radio => radio.SoftwareState == 1 && radio.HardwareState == 1);
            bool hardwareBlocked = radios.Count > 0 && radios.All(radio => radio.HardwareState == 2);
            interfaces.Add(new WifiInterface(id, nativeInterface.Description, enabled, hardwareBlocked, StateName(nativeInterface.State)));

            var active=connections.Connections.FirstOrDefault(item=>item.AdapterId==id && item.Kind==NetworkConnectionKind.Wifi);
            string? activeProfile=nativeInterface.State==1 ? ReadConnectedName(client.Handle,id)??active?.Name : null;
            string? activeSsid=nativeInterface.State==1 ? active?.Ssid : null;
            connectedName??=activeSsid??activeProfile;

            if (scan && enabled != false)
            {
                uint scanResult = WlanScan(client.Handle, ref id, nint.Zero, nint.Zero, nint.Zero);
                accessDenied|=scanResult==5;
                if (scanResult != 0) errors.Add(DescribeError(scanResult, "刷新 Wi-Fi"));
            }

            var profiles = ReadProfileNames(client.Handle, id, errors);
            uint result = WlanGetAvailableNetworkList(client.Handle, ref id, 0, nint.Zero, out nint memory);
            accessDenied|=result==5;
            if (result != 0)
            {
                if (enabled != false) errors.Add(DescribeError(result, "读取 Wi-Fi 网络"));
                if(nativeInterface.State==1 && (activeSsid??activeProfile) is { } connected)
                {
                    networks.Add(new WifiNetwork(id,connected,activeProfile??"",active?.Signal??0,true,
                        true,true,null)
                    {
                        IsInRange = false,
                        SecurityKnown = false
                    });
                }
                continue;
            }
            try
            {
                int count = ReadCount(memory, 1024);
                int size = Marshal.SizeOf<AvailableNetwork>();
                for (int index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<AvailableNetwork>(memory + 8 + index * size);
                    int length = (int)Math.Min(item.Ssid.Length, 32);
                    byte[] bytes = item.Ssid.Bytes.AsSpan(0, length).ToArray();
                    string ssid = Encoding.UTF8.GetString(bytes);
                    if (string.IsNullOrEmpty(ssid)) ssid = "隐藏网络";
                    bool connected = (item.Flags & 1) != 0;
                    string profile = profiles.Contains(item.ProfileName) ? item.ProfileName : "";
                    connected|=nativeInterface.State==1 && (string.Equals(profile,activeProfile,StringComparison.Ordinal) && profile.Length>0
                        || string.Equals(ssid,activeSsid,StringComparison.Ordinal));
                    var network = new WifiNetwork(id, ssid, profile, (int)Math.Min(item.SignalQuality, 100),
                        item.SecurityEnabled != 0, connected, item.Connectable != 0,
                        item.Connectable != 0 ? null : DescribeReason(item.NotConnectableReason))
                    {
                        BssType = item.BssType,
                        SsidBytes = bytes
                    };
                    networks.Add(network);
                    if (connected) connectedName = ssid;
                }
            }
            finally { WlanFreeMemory(memory); }
        }

        bool? radioEnabled = interfaces.Count == 0 || interfaces.All(item => item.RadioEnabled is null)
            ? null
            : interfaces.Any(item => item.RadioEnabled == true);
        if (interfaces.Count == 0) errors.Add("未检测到无线网卡");
        return new WifiSnapshot(interfaces.Count > 0, radioEnabled,
            interfaces.Count > 0 && interfaces.All(item => item.HardwareBlocked), connectedName,
            interfaces,OrderNetworks(networks),
            errors.Count == 0 ? null : string.Join("\n", errors.Distinct())) {Connections=connections,ScanAccessDenied=accessDenied};
    }

    internal static IReadOnlyList<WifiNetwork> OrderNetworks(IEnumerable<WifiNetwork> networks)
        =>networks.Where(item=>item.Connected || item.IsInRange)
            .GroupBy(item=>(item.InterfaceId,item.Ssid,item.Secured))
            .Select(group=>group.OrderByDescending(item=>item.Connected).ThenByDescending(item=>item.HasProfile)
                .ThenByDescending(item=>item.Signal).First())
            .OrderByDescending(item=>item.Connected).ThenByDescending(item=>item.Signal)
            .ThenBy(item=>item.Ssid,StringComparer.CurrentCulture).ToList();

    private static WifiOperationResult SetRadio(bool enabled, Guid? requestedInterface)
    {
        using var client = WlanClient.Open();
        var targets = ReadInterfaces(client.Handle)
            .Where(item => requestedInterface is null || item.Id == requestedInterface.Value).ToList();
        if (targets.Count == 0) return new WifiOperationResult(false, "未找到无线网卡");
        var errors = new List<string>();
        int applied = 0;
        foreach (var target in targets)
        {
            Guid id = target.Id;
            foreach (var radio in ReadRadioStates(client.Handle, id, errors))
            {
                if (enabled && radio.HardwareState == 2)
                {
                    errors.Add("无线网卡的硬件开关已关闭，请先启用硬件开关");
                    continue;
                }
                var state = new PhyRadioState { Index = radio.Index, SoftwareState = enabled ? 1u : 2u };
                uint result = WlanSetInterface(client.Handle, ref id, RadioStateOpcode,
                    (uint)Marshal.SizeOf<PhyRadioState>(), ref state, nint.Zero);
                if (result == 0) applied++;
                else errors.Add(DescribeError(result, "切换 Wi-Fi"));
            }
        }
        if (errors.Count > 0)
        {
            string prefix = applied > 0 ? "部分无线设备已更新。\n" : "";
            return new WifiOperationResult(false, prefix + string.Join("\n", errors.Distinct()));
        }
        return applied > 0
            ? new WifiOperationResult(true, enabled ? "Wi-Fi 已启用" : "Wi-Fi 已关闭")
            : new WifiOperationResult(false, "无线网卡未返回可控制的无线电状态");
    }

    private static WifiOperationResult Connect(WifiNetwork network)
    {
        if (!network.Connectable)
            return new WifiOperationResult(false, network.UnavailableReason ?? "此网络当前无法连接");
        if (network.Secured && !network.HasProfile)
            return new WifiOperationResult(false, "此加密网络尚未保存连接配置，需要先添加密码配置");

        using var client = WlanClient.Open();
        Guid id = network.InterfaceId;
        var parameters = new ConnectionParameters { BssType = network.BssType };
        nint ssidMemory = nint.Zero;
        try
        {
            if (network.HasProfile)
            {
                var errors = new List<string>();
                if (!ReadProfileNames(client.Handle, id, errors).Contains(network.ProfileName))
                    return new WifiOperationResult(false, "已保存的 Wi-Fi 配置已变更，请刷新网络列表");
                parameters.Mode = 0;
                parameters.Profile = network.ProfileName;
            }
            else
            {
                if (network.SsidBytes.Length is < 1 or > 32)
                    return new WifiOperationResult(false, "无法读取此网络的有效 SSID");
                var ssid = new Dot11Ssid { Length = (uint)network.SsidBytes.Length, Bytes = new byte[32] };
                network.SsidBytes.CopyTo(ssid.Bytes, 0);
                ssidMemory = Marshal.AllocHGlobal(Marshal.SizeOf<Dot11Ssid>());
                Marshal.StructureToPtr(ssid, ssidMemory, false);
                parameters.Mode = 3; // wlan_connection_mode_discovery_unsecure
                parameters.Ssid = ssidMemory;
            }
            Check(WlanConnect(client.Handle, ref id, ref parameters, nint.Zero), "连接 Wi-Fi");
            return new WifiOperationResult(true, "正在连接，请稍后刷新查看连接结果");
        }
        finally
        {
            if (ssidMemory != nint.Zero) Marshal.FreeHGlobal(ssidMemory);
        }
    }

    private static List<InterfaceInfo> ReadInterfaces(nint handle)
    {
        Check(WlanEnumInterfaces(handle, nint.Zero, out nint memory), "读取无线网卡");
        try
        {
            int count = ReadCount(memory, 64);
            int size = Marshal.SizeOf<InterfaceInfo>();
            var result = new List<InterfaceInfo>(count);
            for (int index = 0; index < count; index++)
                result.Add(Marshal.PtrToStructure<InterfaceInfo>(memory + 8 + index * size));
            return result;
        }
        finally { WlanFreeMemory(memory); }
    }

    private static List<PhyRadioState> ReadRadioStates(nint handle, Guid id, List<string> errors)
    {
        uint result = WlanQueryInterface(handle, ref id, RadioStateOpcode, nint.Zero,
            out uint bytes, out nint memory, out _);
        if (result != 0)
        {
            errors.Add(DescribeError(result, "读取 Wi-Fi 开关"));
            return [];
        }
        try
        {
            if (bytes < 4) throw new InvalidOperationException("无线电状态数据不完整");
            int count = ReadCount(memory, 64);
            int size = Marshal.SizeOf<PhyRadioState>();
            if (bytes < 4 + count * size) throw new InvalidOperationException("无线电状态数据长度无效");
            var radios = new List<PhyRadioState>(count);
            for (int index = 0; index < count; index++)
                radios.Add(Marshal.PtrToStructure<PhyRadioState>(memory + 4 + index * size));
            return radios;
        }
        finally { WlanFreeMemory(memory); }
    }

    private static HashSet<string> ReadProfileNames(nint handle, Guid id, List<string> errors)
    {
        uint result = WlanGetProfileList(handle, ref id, nint.Zero, out nint memory);
        if (result != 0)
        {
            errors.Add(DescribeError(result, "读取已保存的 Wi-Fi"));
            return [];
        }
        try
        {
            int count = ReadCount(memory, 1024);
            var profiles = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                string name = ReadFixedString(memory + 8 + index * 516, 256);
                if (!string.IsNullOrWhiteSpace(name)) profiles.Add(name);
            }
            return profiles;
        }
        finally { WlanFreeMemory(memory); }
    }

    private static string? ReadConnectedName(nint handle, Guid id)
    {
        uint result = WlanQueryInterface(handle, ref id, CurrentConnectionOpcode, nint.Zero,
            out uint bytes, out nint memory, out _);
        if (result != 0) return null;
        try
        {
            return bytes >= 520 ? ReadFixedString(memory + 8, 256) : null;
        }
        finally { WlanFreeMemory(memory); }
    }

    private static string ReadFixedString(nint pointer, int maximumCharacters)
    {
        string value = Marshal.PtrToStringUni(pointer, maximumCharacters) ?? "";
        int terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }

    private static int ReadCount(nint pointer, int maximum)
    {
        if (pointer == nint.Zero) throw new InvalidOperationException("Wi-Fi 服务返回了空数据");
        int count = Marshal.ReadInt32(pointer);
        if (count < 0 || count > maximum) throw new InvalidOperationException("Wi-Fi 服务返回的数据数量无效");
        return count;
    }

    private static string StateName(uint state) => state switch
    {
        1 => "已连接",
        2 => "正在连接临时网络",
        3 => "正在断开",
        4 => "未连接",
        5 => "正在关联",
        6 => "正在发现",
        7 => "正在验证",
        _ => "状态未知"
    };

    private static string DescribeReason(uint reason)
    {
        var buffer = new StringBuilder(1024);
        return WlanReasonCodeToString(reason, (uint)buffer.Capacity, buffer, nint.Zero) == 0
            ? buffer.ToString()
            : $"此网络无法连接（{reason}）";
    }

    private static string DescribeError(uint code, string operation)
    {
        return code switch
        {
            5 => $"{operation}需要 Windows 授予 Wi-Fi/位置信息访问权限，或此操作被设备策略拒绝",
            1062 => "Windows WLAN 自动配置服务尚未运行",
            1260 => "Wi-Fi 操作被设备策略阻止",
            _ => $"{operation}失败，{new Win32Exception((int)code).Message}（{code}）"
        };
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new Win32Exception((int)result, DescribeError(result, operation));
    }

    public static void ValidateLayouts()
    {
        if (Marshal.SizeOf<InterfaceInfo>() != 532 || Marshal.SizeOf<AvailableNetwork>() != 628 ||
            Marshal.SizeOf<Dot11Ssid>() != 36 || Marshal.SizeOf<PhyRadioState>() != 12 ||
            Marshal.SizeOf<ConnectionParameters>() != (IntPtr.Size == 8 ? 40 : 24))
            throw new InvalidOperationException("原生 WLAN 结构布局不匹配");
    }

    private sealed class WlanClient(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public static WlanClient Open()
        {
            Check(WlanOpenHandle(2, nint.Zero, out _, out nint handle), "访问 Wi-Fi 服务");
            return new WlanClient(handle);
        }
        public void Dispose() => WlanCloseHandle(Handle, nint.Zero);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Ssid;
        public uint BssType, BssidCount;
        public int Connectable;
        public uint NotConnectableReason, PhyCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] PhyTypes;
        public int MorePhyTypes;
        public uint SignalQuality;
        public int SecurityEnabled;
        public uint Authentication, Cipher, Flags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PhyRadioState
    {
        public uint Index, SoftwareState, HardwareState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionParameters
    {
        public uint Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Profile;
        public nint Ssid, DesiredBssids;
        public uint BssType, Flags;
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiatedVersion, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, uint opcode, nint reserved, out uint dataSize, out nint data, out uint opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern uint WlanSetInterface(nint handle, ref Guid id, uint opcode, uint size, ref PhyRadioState state, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(nint handle, ref Guid id, uint flags, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(nint handle, ref Guid id, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(nint handle, ref Guid id, ref ConnectionParameters parameters, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(nint handle, ref Guid id, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(nint handle, ref Guid id, nint ssid, nint informationElementData, nint reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanReasonCodeToString(uint reason, uint bufferSize, StringBuilder buffer, nint reserved);
}
