using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace GlassDock.Services;

internal enum NetworkConnectionKind { Other, Wifi, Ethernet }

internal sealed record NetworkConnection(Guid AdapterId,string Name,NetworkConnectionKind Kind,
    NetworkConnectivityLevel Connectivity,string? Ssid,int? Signal)
{
    internal string Status => Connectivity switch {
        NetworkConnectivityLevel.InternetAccess => "已连接 · Internet",
        NetworkConnectivityLevel.ConstrainedInternetAccess => "已连接 · 需要登录",
        _ => "已连接 · 无 Internet"
    };
}

internal sealed record NetworkConnectionSnapshot(bool Known,Guid? PreferredAdapter,
    IReadOnlyList<NetworkConnection> Connections)
{
    internal string Glyph => !Known ? "\uE9CE" : !Connections.Any(item=>item.Connectivity==NetworkConnectivityLevel.InternetAccess) ? "\uE774" :
        Primary?.Kind==NetworkConnectionKind.Ethernet ? "\uE839" :
        Primary?.Kind==NetworkConnectionKind.Wifi ? "\uE701" : "\uE774";
    internal NetworkConnection? Primary => Connections.Where(item=>item.Kind!=NetworkConnectionKind.Other)
        .OrderByDescending(item=>item.AdapterId==PreferredAdapter)
        .ThenByDescending(item=>item.Connectivity==NetworkConnectivityLevel.InternetAccess)
        .ThenByDescending(item=>item.Kind==NetworkConnectionKind.Ethernet).FirstOrDefault()??Connections.FirstOrDefault();
    internal string Description => Known ? Primary is { } primary ? primary.Name+" · "+primary.Status : "未连接网络" : "网络状态未知";
}

/// <summary>Adapter-bound NLM/WinRT connection state, independent of Wi-Fi scan permission.</summary>
internal static class NetworkConnections
{
    private static readonly object Sync=new();
    private static Task<NetworkConnectionSnapshot>? _pending;
    internal static Task<NetworkConnectionSnapshot> ReadAsync()
    {
        lock(Sync) {
            if(_pending is {IsCompleted:false})return _pending;
            return _pending=Task.Run(Read);
        }
    }

    internal static NetworkConnectionSnapshot Read()
    {
        try {
            var up=NetworkInterface.GetAllNetworkInterfaces().Where(item=>item.OperationalStatus==OperationalStatus.Up)
                .Select(item=>Guid.TryParse(item.Id,out var id)?id:Guid.Empty).ToHashSet();
            var connections=new List<NetworkConnection>();
            foreach(var profile in NetworkInformation.GetConnectionProfiles()) {
                if(profile.NetworkAdapter is not { } adapter || !up.Contains(adapter.NetworkAdapterId))continue;
                var kind=profile.IsWlanConnectionProfile ? NetworkConnectionKind.Wifi :
                    adapter.IanaInterfaceType==6 ? NetworkConnectionKind.Ethernet : NetworkConnectionKind.Other;
                string? ssid=null;
                if(kind==NetworkConnectionKind.Wifi) {
                    try {ssid=profile.WlanConnectionProfileDetails?.GetConnectedSsid();}
                    catch(Exception ex) when(ex is COMException or UnauthorizedAccessException) { }
                }
                connections.Add(new(adapter.NetworkAdapterId,profile.ProfileName,kind,
                    profile.GetNetworkConnectivityLevel(),ssid,profile.GetSignalBars() is byte bars ? bars*20 : null));
            }
            return new(true,NetworkInformation.GetInternetConnectionProfile()?.NetworkAdapter?.NetworkAdapterId,connections);
        }
        catch(Exception ex) when(ex is COMException or NetworkInformationException or UnauthorizedAccessException) {
            return new(false,null,[]);
        }
    }
}
