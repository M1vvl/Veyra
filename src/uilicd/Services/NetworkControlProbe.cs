using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Windows.Networking.Connectivity;

namespace GlassDock.Services;

/// <summary>Read-only live WLAN/NLM checks and isolated UI regressions, without report files.</summary>
internal static class NetworkControlProbe
{
    internal static int Run()
    {
        int result=1,checks=0;
        void Check(bool passed,string name) {if(!passed)throw new InvalidOperationException(name);checks++;}
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=> {
            SidebarWindow? sidebar=null;QuickControlsWindow? panel=null;
            try {
                WifiService.ValidateLayouts();checks++;
                Guid adapter=Guid.NewGuid();
                WifiNetwork Row(string name,int signal,bool connected=false)=>new(adapter,name,name,signal,false,connected,true,null);
                var connected=Row("Connected",10,true);
                var high=Row("High",95);var medium=Row("Medium",65);var low=Row("Low",20);
                var ordered=WifiService.OrderNetworks(new[] {low,medium,connected,high,high,Row("Old",100) with {IsInRange=false}});
                Check(ordered.Select(item=>item.Ssid).SequenceEqual(new[] {"Connected","High","Medium","Low"}),"Connected-first, descending signal, duplicate/stale filtering");
                var wired=new NetworkConnection(adapter,"Ethernet test",NetworkConnectionKind.Ethernet,NetworkConnectivityLevel.InternetAccess,null,null);
                var ethernet=new NetworkConnectionSnapshot(true,adapter,new[] {wired});
                Check(ethernet.Glyph=="\uE839","Ethernet native glyph");
                Check(new NetworkConnectionSnapshot(true,null,[]).Glyph=="\uE774","Disconnected globe glyph");
                Check((ethernet with {Connections=new[] {wired with {Connectivity=NetworkConnectivityLevel.LocalAccess}}}).Glyph=="\uE774","No Internet globe glyph");
                Check((ethernet with {Connections=new[] {wired with {Kind=NetworkConnectionKind.Wifi}}}).Glyph=="\uE701","Wi-Fi native glyph");
                sidebar=new SidebarWindow();panel=new QuickControlsWindow(sidebar,0);
                var type=typeof(QuickControlsWindow);var flags=BindingFlags.NonPublic|BindingFlags.Instance;
                await ((Task)type.GetMethod("BuildWifi",flags)!.Invoke(panel,new object[] {0})!).WaitAsync(TimeSpan.FromSeconds(10));
                var snapshot=new WifiSnapshot(true,true,false,"Connected",new[] {new WifiInterface(adapter,"test",true,false,"已连接")},ordered,null) {Connections=ethernet};
                var update=type.GetMethod("UpdateNetworks",flags)!;
                update.Invoke(panel,new object[] {snapshot});
                var list=(StackPanel)type.GetField("_networkList",flags)!.GetValue(panel)!;
                var controls=list.Children.OfType<Grid>().ToArray();
                Check(controls.Select(item=>((TextBlock)((StackPanel)item.Children[0]).Children[0]).Text)
                    .SequenceEqual(ordered.Select(item=>item.Ssid)),"UI follows connected/signal order");
                Check(((TextBlock)((StackPanel)controls[0].Children[0]).Children[1]).Text=="已连接","Connected row caption");
                Check(System.Windows.Automation.AutomationProperties.GetName((Button)controls[0].Children[1])=="断开 Connected","Disconnect action");
                int surfaces=((System.Collections.ICollection)type.GetField("_cards",flags)!.GetValue(panel)!).Count;
                for(int index=0;index<20;index++)update.Invoke(panel,new object[] {snapshot});
                Check(ReferenceEquals(controls[0],list.Children.OfType<Grid>().First()),"Signal refresh reuses row controls");
                Check(((System.Collections.ICollection)type.GetField("_cards",flags)!.GetValue(panel)!).Count==surfaces,"Refresh retains bounded material surfaces");
                var wiredList=(StackPanel)type.GetField("_wiredList",flags)!.GetValue(panel)!;
                Check(((TextBlock)wiredList.Children[1]).Text.Contains("Ethernet test · 已连接"),"Ethernet display");
                update.Invoke(panel,new object[] {snapshot with {Networks=new[] {high}}});
                Check(list.Children.OfType<Grid>().Count()==1,"Expired scan rows are removed");
                var live=await WifiService.GetSnapshotAsync(true);
                Check(live.Networks.All(item=>item.Connected || item.IsInRange),"Live output excludes saved-only history");
                Console.WriteLine(JsonSerializer.Serialize(new {pass=true,checks,
                    live=new {live.IsAvailable,live.RadioEnabled,live.ConnectedName,live.ScanAccessDenied,
                        networks=live.Networks.Select(item=>new {item.Ssid,item.Signal,item.Connected,item.IsInRange}),
                        connections=live.Connections.Connections.Select(item=>new {item.Name,kind=item.Kind.ToString(),item.Status}),
                        glyph=live.Connections.Glyph,live.Error}}));
                result=0;
            }
            catch(Exception ex) {Console.WriteLine(JsonSerializer.Serialize(new {pass=false,checks,error=ex.ToString()}));}
            finally {panel?.Close();sidebar?.Close();app.Shutdown(result);}
        };
        app.Run();return result;
    }
}
