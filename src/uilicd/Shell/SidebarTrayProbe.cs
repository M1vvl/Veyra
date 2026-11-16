using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDock.Shell;

/// <summary>Only the real sidebar/control-center tray UI and owned notification fixtures.</summary>
internal static class SidebarTrayProbe
{
    internal static int Run(string path)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Styles.GlassMenuSurface.Install(app);
        Program.Prefs=Preferences.Load(); Program.Prefs.SidebarPinned=true;
        Program.Preview=Program.CapturePreview=true; PreferencesPersistence.WritesSuspendedForProbe=true;
        using var host=new TrayHostService(app.Dispatcher); Program.TrayHost=host;
        using var fixture=new HwndSource(new HwndSourceParameters("GlassDock owned flashing tray fixture") {WindowStyle=unchecked((int)0x80000000),Width=1,Height=1});
        int callbacks=0; fixture.AddHook((nint h,int m,nint w,nint l,ref bool handled)=> {if(m==0x8B00) {callbacks++; handled=true;} return 0;});
        var results=new List<object>(); string? failure=null;
        void Check(string name,bool pass,object? detail=null) {results.Add(new {name,pass,detail}); if(!pass)throw new InvalidOperationException(name);}
        TrayProtocol.WireIcon Packet(uint id,nint icon,uint flags=7)=>new() {Window=unchecked((uint)fixture.Handle.ToInt64()),Id=id,Flags=flags,CallbackMessage=0x8B00,IconHandle=unchecked((uint)icon.ToInt64()),Tip="闪烁测试 "+id};
        app.Dispatcher.BeginInvoke(new Action(async()=> {
            try
            {
                nint icon=LoadIcon(0,32516);
                for(uint id=1;id<=3;id++)host.ReceiveForProbe(TrayProtocol.Add,Packet(id,icon));
                var version=Packet(2,0,0); version.Version=4; host.ReceiveForProbe(TrayProtocol.SetVersion,version);
                Program.Sidebar=new SidebarWindow(); Program.Sidebar.Show(); Program.Sidebar.RevealForCapture(); Program.Sidebar.OpenPanel(ControlSection.Apps);
                await Task.Delay(220);
                var controls=app.Windows.OfType<QuickControlsWindow>().Single(); controls.UpdateLayout();
                var panel=Descendants<TrayPanel>(controls).Single(); panel.RefreshForProbe(); controls.UpdateLayout();
                var initial=panel.SlotsForProbe;
                Check("Actual sidebar opens its own tray panel with three registered fixture slots",initial.Length==3 && controls.CurrentSection==ControlSection.Apps);
                var points=initial.Select(slot=>slot.Button.PointToScreen(new Point())).ToArray();
                bool stable=true;
                for(int cycle=0;cycle<8;cycle++)
                {
                    host.ReceiveForProbe(TrayProtocol.Modify,Packet(2,0,TrayProtocol.Icon)); panel.RefreshForProbe(); controls.UpdateLayout();
                    var off=panel.SlotsForProbe;
                    stable&=off.Length==3 && off[1].Image.Source is null && off.Select(slot=>slot.Key).SequenceEqual(initial.Select(slot=>slot.Key));
                    for(int i=0;i<off.Length;i++)stable&=ReferenceEquals(off[i].Button,initial[i].Button) && (off[i].Button.PointToScreen(new Point())-points[i]).Length<.01;
                    await Task.Delay(34);
                    host.ReceiveForProbe(TrayProtocol.Modify,Packet(2,icon,TrayProtocol.Icon)); panel.RefreshForProbe(); controls.UpdateLayout();
                    stable&=panel.SlotsForProbe[1].Image.Source is not null; await Task.Delay(34);
                }
                Check("Eight flash cycles preserve exact slot positions and button instances",stable,new {cycles=8,slots=panel.SlotsForProbe.Length});
                var offPacket=Packet(2,0,TrayProtocol.Icon); host.ReceiveForProbe(TrayProtocol.Modify,offPacket); panel.RefreshForProbe();
                var blinking=host.Snapshot.Single(entry=>entry.IconId==2);
                Check("Blank flash frame retains registered metadata and callback version",blinking.HasVisualRegistration && blinking.Image is null && blinking.Version==4 && blinking.CallbackMessage==0x8B00);
                int before=callbacks; initial[1].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
                Check("A blank flashing slot still sends the real primary callback",callbacks>before);
                host.ReceiveForProbe(TrayProtocol.Modify,Packet(2,icon,TrayProtocol.Icon));
                var hidden=Packet(2,0,TrayProtocol.State); hidden.State=hidden.StateMask=1;
                host.ReceiveForProbe(TrayProtocol.Modify,hidden); panel.RefreshForProbe();
                Check("Transient native hidden-state flashes preserve their original blank slot",panel.SlotsForProbe.Length==3 && panel.SlotsForProbe[1].Image.Source is null);
                hidden.State=0; host.ReceiveForProbe(TrayProtocol.Modify,hidden); panel.RefreshForProbe();
                host.ReceiveForProbe(TrayProtocol.Delete,Packet(2,0,0)); panel.RefreshForProbe();
                Check("Delete/add flashing keeps a reserved slot for a live registered owner",panel.SlotsForProbe.Length==3 && panel.SlotsForProbe[1].Image.Source is null);
                host.ReceiveForProbe(TrayProtocol.Add,Packet(2,icon)); panel.RefreshForProbe();
                Check("Re-add restores the same view and sequence rather than appending a new icon",ReferenceEquals(panel.SlotsForProbe[1].Button,initial[1].Button));
                host.ReceiveForProbe(TrayProtocol.Modify,Packet(9,0,TrayProtocol.Message)); panel.RefreshForProbe();
                Check("Never-registered missing images do not create phantom slots",panel.SlotsForProbe.Length==3);
                host.ReceiveForProbe(TrayProtocol.Delete,Packet(2,0,0)); await Task.Delay(850); panel.RefreshForProbe(); controls.UpdateLayout();
                Check("A real delete removes only its slot and preserves neighboring views",panel.SlotsForProbe.Length==2 && ReferenceEquals(panel.SlotsForProbe[0].Button,initial[0].Button) && ReferenceEquals(panel.SlotsForProbe[1].Button,initial[2].Button));
                Check("Sidebar and control-center windows stay visible through tray updates",Program.Sidebar.IsVisible && controls.IsVisible);
                for(uint id=10;id<20;id++)host.ReceiveForProbe(TrayProtocol.Add,Packet(id,icon));
                panel.RefreshForProbe(); controls.UpdateLayout();
                var packed=panel.SlotsForProbe;
                double firstRow=packed[0].Button.TranslatePoint(new Point(),panel).Y;
                int rowCount=packed.Count(slot=>Math.Abs(slot.Button.TranslatePoint(new Point(),panel).Y-firstRow)<.1);
                Check("Tray fills all nine available columns without extra cell margins",rowCount==9,new {firstRowIcons=rowCount});
                Check("Tray icons retain native size and independent hit targets",packed.All(slot=>slot.Image.Width==16 && slot.Button.Width==32));
                controls.Close(); Program.Sidebar.Close();
            }
            catch(Exception exception) {failure=exception.ToString();}
            finally
            {
                foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();
                File.WriteAllText(path,JsonSerializer.Serialize(new {timestamp=DateTimeOffset.Now,scope="sidebar/control-center tray only; owned fixtures; no real application registrations changed",results,failure},new JsonSerializerOptions {WriteIndented=true}));
                app.Shutdown(failure is null?0:1);
            }
        }));
        return app.Run();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) {var child=VisualTreeHelper.GetChild(node,i); if(child is T item)yield return item; foreach(var descendant in Descendants<T>(child))yield return descendant;}
    }
    [DllImport("user32.dll",EntryPoint="LoadIconW")] private static extern nint LoadIcon(nint instance,nint resource);
}
