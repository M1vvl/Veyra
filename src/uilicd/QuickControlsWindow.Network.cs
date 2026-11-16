using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Services;
using Windows.Devices.WiFi;

namespace GlassDock;

internal sealed partial class QuickControlsWindow
{
    private sealed class NetworkRow(WifiNetwork network,Grid panel,TextBlock detail,Button action,TextBlock caption,GlassCard material)
    {
        internal WifiNetwork Network=network;
        internal readonly Grid Panel=panel;
        internal readonly TextBlock Detail=detail,Caption=caption;
        internal readonly Button Action=action;
        internal readonly GlassCard Material=material;
    }
    private readonly Dictionary<(Guid,string,bool),NetworkRow> _networkRows=[];
    private WifiChangeSubscription? _networkSubscription;
    private DispatcherTimer? _networkTimer;
    private StackPanel? _networkList,_wiredList;
    private TextBlock? _wifiState,_networkEmpty;
    private Button? _wifiRadio,_scanAccess;
    private TextBlock? _wifiRadioCaption;
    private bool _networkReading,_networkReadAgain,_networkScanRequested,_networkOperating;
    private long _lastNetworkScan;

    private async Task BuildWifi(int generation)
    {
        _wiredList=Card("以太网");
        _wifiState=Ui.Text("读取中",12,Secondary);
        var heading=Card("Wi-Fi",_wifiState);
        var actions=new WrapPanel();
        _wifiRadio=ActionButton("切换 Wi-Fi","切换 Wi-Fi",()=>Run(ToggleWifiRadioAsync),135,34);
        _wifiRadioCaption=FindCaption(_wifiRadio);
        actions.Children.Add(_wifiRadio);
        actions.Children.Add(ActionButton("刷新","刷新附近网络",()=>Run(()=>RefreshNetworksAsync(true)),135,34));
        _scanAccess=ActionButton("位置设置","打开 Wi-Fi 扫描访问设置",()=>Run(RequestScanAccessAsync),135,34);
        _scanAccess.Visibility=Visibility.Collapsed;
        actions.Children.Add(_scanAccess);heading.Children.Add(actions);
        _networkList=Card("附近网络");
        _networkEmpty=Ui.Text("正在读取网络",12,Secondary);_networkList.Children.Add(_networkEmpty);
        var subscription=await Task.Run(()=>new WifiChangeSubscription(NetworkChanged));
        if(!Current(generation)) {subscription.Dispose();return;}
        _networkSubscription=subscription;
        if(_networkTimer is null) {
            _networkTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
            _networkTimer.Tick+=(_,_)=>Run(()=>RefreshNetworksAsync(false));
        }
        await RefreshNetworksAsync(true);
        if(Current(generation))_networkTimer.Start();
    }

    private static TextBlock FindCaption(Button button)
        =>((GlassCard)button.Content).Content is Border {Child:TextBlock caption}?caption:throw new InvalidOperationException("Missing button caption");

    private void NetworkChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        _=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=> {
            if(!_closed && _section==ControlSection.Wifi && IsVisible)Run(()=>RefreshNetworksAsync(false));
        }));
    }

    private void StopNetworkWatching()
    {
        _networkTimer?.Stop();_networkSubscription?.Dispose();_networkSubscription=null;
        _networkReadAgain=false;_networkScanRequested=false;
        _networkRows.Clear();_networkList=null;_wiredList=null;
        _wifiState=null;_wifiRadio=null;_wifiRadioCaption=null;_scanAccess=null;_networkEmpty=null;
    }

    private async Task RefreshNetworksAsync(bool scan)
    {
        _networkScanRequested|=scan;
        if(_networkReading) {_networkReadAgain=true;return;}
        _networkReading=true;
        try {
            do {
                _networkReadAgain=false;
                int generation=_generation;
                bool requestScan=_networkScanRequested || (_wifiSnapshot?.ScanAccessDenied!=true && Environment.TickCount64-_lastNetworkScan>=12000);
                _networkScanRequested=false;
                if(requestScan)_lastNetworkScan=Environment.TickCount64;
                var snapshot=await WifiService.GetSnapshotAsync(requestScan);
                if(Current(generation) && _section==ControlSection.Wifi)UpdateNetworks(snapshot);
            } while(_networkReadAgain && !_closed && _section==ControlSection.Wifi);
        }
        finally {_networkReading=false;}
    }

    private void UpdateNetworks(WifiSnapshot snapshot)
    {
        if(_networkList is null || _wiredList is null || _wifiState is null || _wifiRadio is null)return;
        _wifiSnapshot=snapshot;
        // Keep the same controls and glass surfaces during signal/connection updates.
        // This also preserves pointer targets and the scroll position.
        string wiredText=string.Join("\n",snapshot.Connections.Connections.Where(item=>item.Kind==NetworkConnectionKind.Ethernet)
            .Select(item=>item.Name+" · "+item.Status));
        if(_wiredList.Children.Count==1)_wiredList.Children.Add(Ui.Text("",12,Secondary));
        ((TextBlock)_wiredList.Children[1]).Text=string.IsNullOrEmpty(wiredText)?snapshot.Connections.Known?"未连接网线":"状态未知":wiredText;
        _wifiState.Text=!snapshot.IsAvailable?"不可用":snapshot.HardwareBlocked?"硬件已关闭":
            snapshot.RadioEnabled switch {true=>snapshot.ScanAccessDenied?"扫描受限":"已开启",false=>"已关闭",_=>"状态未知"};
        _wifiState.ToolTip=snapshot.Error;
        _wifiRadio.IsEnabled=snapshot.IsAvailable && !snapshot.HardwareBlocked && !_networkOperating;
        if(_wifiRadioCaption is not null)_wifiRadioCaption.Text=snapshot.RadioEnabled==true?"关闭 Wi-Fi":"开启 Wi-Fi";
        _scanAccess!.Visibility=snapshot.ScanAccessDenied?Visibility.Visible:Visibility.Collapsed;
        _scanAccess.ToolTip=snapshot.Error;
        _networkEmpty!.Text=snapshot.ScanAccessDenied?"需要允许位置访问才能扫描附近网络":
            snapshot.RadioEnabled==false?"Wi-Fi 已关闭":snapshot.Error is not null?"暂时无法读取网络":"暂无可用网络";
        _networkEmpty.ToolTip=snapshot.Error;
        _networkEmpty.Visibility=snapshot.Networks.Count==0?Visibility.Visible:Visibility.Collapsed;
        var keys=snapshot.Networks.Select(NetworkKey).ToHashSet();
        foreach(var key in _networkRows.Keys.Where(key=>!keys.Contains(key)).ToArray()) {
            var removed=_networkRows[key];_networkList.Children.Remove(removed.Panel);
            removed.Material.Dispose();_cards.Remove(removed.Material);_networkRows.Remove(key);
        }
        int position=2; // Card heading and empty-state text.
        foreach(var network in snapshot.Networks) {
            var key=NetworkKey(network);
            if(!_networkRows.TryGetValue(key,out var row)) {
                row=CreateNetworkRow(network);_networkRows.Add(key,row);
            }
            row.Network=network;
            row.Detail.Text=network.Connected?"已连接":$"信号 {network.Signal}% · {(network.Secured?"加密":"开放")}";
            row.Caption.Text=network.Connected?"断开":"连接";
            AutomationProperties.SetName(row.Action,(network.Connected?"断开 ":"连接 ")+network.Ssid);
            row.Action.IsEnabled=!_networkOperating && (network.Connected || network.Connectable && (network.HasProfile || !network.Secured));
            row.Action.ToolTip=!network.HasProfile && network.Secured?"此网络需要首次密码配置":network.UnavailableReason;
            ToolTipService.SetShowOnDisabled(row.Action,true);
            if(_networkList.Children.IndexOf(row.Panel)!=position) {
                _networkList.Children.Remove(row.Panel);_networkList.Children.Insert(position,row.Panel);
            }
            position++;
        }
        FitToContent();
    }

    private static (Guid,string,bool) NetworkKey(WifiNetwork network)=>(network.InterfaceId,network.Ssid,network.Secured);

    private NetworkRow CreateNetworkRow(WifiNetwork network)
    {
        var panel=new Grid {Margin=new Thickness(0,5,0,3),MinHeight=46};
        panel.ColumnDefinitions.Add(new ColumnDefinition());panel.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(88)});
        var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,7,0)};
        var name=Ui.Text(network.Ssid,13);name.FontWeight=FontWeights.SemiBold;labels.Children.Add(name);
        var detail=Ui.Text("",11,Secondary);labels.Children.Add(detail);panel.Children.Add(labels);
        var caption=Ui.Text("连接",12);caption.HorizontalAlignment=HorizontalAlignment.Center;caption.VerticalAlignment=VerticalAlignment.Center;
        NetworkRow? row=null;
        var button=MaterialButton(caption,"连接 "+network.Ssid,()=>Run(()=>ChangeNetworkAsync(row!)),84,32);
        Grid.SetColumn(button,1);panel.Children.Add(button);
        row=new(network,panel,detail,button,caption,(GlassCard)button.Content);
        return row;
    }

    private async Task ChangeNetworkAsync(NetworkRow row)
    {
        if(_networkOperating)return;
        var network=row.Network;
        int generation=_generation;
        _networkOperating=true;
        try {
            if(_wifiSnapshot is { } previous)UpdateNetworks(previous);
            var result=network.Connected?await WifiService.DisconnectAsync(network.InterfaceId):await WifiService.ConnectAsync(network);
            if(!Current(generation))return;
            Status(result.Message);
            if(!result.Succeeded)return;
            long deadline=Environment.TickCount64+15000;
            do {
                var observed=await WifiService.GetSnapshotAsync();
                if(!Current(generation))return;
                UpdateNetworks(observed);
                bool connected=observed.Networks.Any(item=>NetworkKey(item)==NetworkKey(network) && item.Connected);
                bool disconnected=observed.Interfaces.Any(item=>item.Id==network.InterfaceId && item.State=="未连接");
                if(network.Connected?disconnected:connected) {Status(network.Connected?"已断开 "+network.Ssid:"已连接 "+network.Ssid);return;}
                await Task.Delay(400);
            } while(Current(generation) && Environment.TickCount64<deadline);
            if(Current(generation))Status("连接状态尚未确认");
        }
        finally {
            _networkOperating=false;
            if(!_closed && _section==ControlSection.Wifi && _wifiSnapshot is { } latest)UpdateNetworks(latest);
        }
    }

    private async Task ToggleWifiRadioAsync()
    {
        if(_networkOperating || _wifiSnapshot is null)return;
        int generation=_generation;
        _networkOperating=true;
        try {
            var result=await WifiService.SetRadioAsync(_wifiSnapshot.RadioEnabled!=true);
            if(Current(generation)) {Status(result.Message);await RefreshNetworksAsync(true);}
        }
        finally {_networkOperating=false;if(Current(generation) && _wifiSnapshot is { } latest)UpdateNetworks(latest);}
    }

    private async Task RequestScanAccessAsync()
    {
        int generation=_generation;
        var access=await WiFiAdapter.RequestAccessAsync();
        if(!Current(generation))return;
        if(access!=WiFiAccessStatus.Allowed)Process.Start(new ProcessStartInfo("ms-settings:privacy-location") {UseShellExecute=true});
        await RefreshNetworksAsync(true);
    }
}
