using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDock.Shell;

/// <summary>Exercises the public Shell API and the production tray receiver using owned icons only.</summary>
internal static class TrayHostProbe
{
    private sealed record Callback(uint Message, long WParam, long LParam);
    private const uint CallbackBase=0x8200;
    private static readonly uint[] Ids=[0x601,0x602,0x603];

    internal static int Run(string reportPath)
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        using var menuDismissal=new ContextMenuDismissal(app.Dispatcher);
        var results=new List<object>();
        var callbacks=new List<Callback>();
        object? external=null;
        string? failure=null;
        bool finished=false;
        var fixture=new HwndSource(new HwndSourceParameters("GlassDock tray protocol fixture") { WindowStyle=unchecked((int)0x80000000),Width=1,Height=1 });
        fixture.AddHook((nint hwnd,int message,nint wParam,nint lParam,ref bool handled)=> {
            if((uint)message>=CallbackBase && (uint)message<CallbackBase+3)
            {
                callbacks.Add(new Callback((uint)message,wParam.ToInt64(),lParam.ToInt64()));
                handled=true;
            }
            return 0;
        });
        TrayHostService? host=null;
        var watchdog=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(18) };
        watchdog.Tick+=(_,_)=> { failure="Tray probe timed out."; Finish(); };
        void Check(string name,bool pass,object? detail=null)
        {
            results.Add(new { name,pass,detail });
            if(!pass) throw new InvalidOperationException(name);
        }
        void Finish()
        {
            if(finished) return;
            finished=true;
            watchdog.Stop();
            for(int i=0;i<Ids.Length;i++) { var data=Data(fixture.Handle,i); Shell_NotifyIcon(2,ref data); }
            host?.Dispose(); host=null;
            fixture.Dispose();
            string fullPath=Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string output=JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,mode="public Shell API, owned fixture callbacks only",results,external,callbacks,failure },new JsonSerializerOptions { WriteIndented=true });
            File.WriteAllText(fullPath,output); Console.WriteLine(output);
            app.Shutdown(failure is null?0:1);
        }
        app.Dispatcher.BeginInvoke(new Action(async()=> {
            try
            {
                Check("Native NOTIFYICONDATA x64 layout",Marshal.SizeOf<NotifyIconData>()==976,Marshal.SizeOf<NotifyIconData>());
                host=new TrayHostService();
                host.Start();
                Check("Production receiver preferred by Shell",host.IsPreferredHost);
                for(int i=0;i<Ids.Length;i++)
                {
                    var data=Data(fixture.Handle,i);
                    Check("Shell_NotifyIcon add "+i,Shell_NotifyIcon(0,ref data));
                    data.Version=(uint)(i==0?0:i+2);
                    Check("Shell_NotifyIcon set version "+data.Version,Shell_NotifyIcon(4,ref data));
                }
                var updated=Data(fixture.Handle,1); updated.Flags=4; updated.Tip="GlassDock fixture modified";
                Check("Shell_NotifyIcon modify",Shell_NotifyIcon(1,ref updated));
                await Task.Delay(4000);
                // Production API integration is completed below after registration settles.
                await VerifyRegisteredAsync(host,fixture.Handle,callbacks,Check,value=>external=value);
                await VerifyPanelDetachAsync(host,Check);
                var menu=new ContextMenu { Items={new MenuItem { Header="Owned menu fixture" }},Placement=System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,HorizontalOffset=20,VerticalOffset=20 };
                menu.IsOpen=true; await Task.Delay(40);
                Check("Menu input observers attach only while an owned menu is open",menuDismissal.OpenMenuCount==1 && menuDismissal.HookCount==2,new { menuDismissal.OpenMenuCount,menuDismissal.HookCount });
                menu.IsOpen=false; await Task.Delay(40);
                Check("Closed menus release all native input observers",menuDismissal.OpenMenuCount==0 && menuDismissal.HookCount==0,new { menuDismissal.OpenMenuCount,menuDismissal.HookCount });
                for(int i=0;i<Ids.Length;i++)
                {
                    var data=Data(fixture.Handle,i);
                    Check("Shell_NotifyIcon delete "+i,Shell_NotifyIcon(2,ref data));
                }
                await Task.Delay(80);
                Check("Owned icons removed",!host.Snapshot.Any(icon=>icon.WindowHandle==fixture.Handle && Ids.Contains(icon.IconId)));
            }
            catch(Exception ex) { failure=ex.ToString(); }
            finally { Finish(); }
        }));
        watchdog.Start();
        return app.Run();
    }

    private static async Task VerifyPanelDetachAsync(TrayHostService host,Action<string,bool,object?> check)
    {
        int errors=0;
        var panel=new TrayPanel(host,_=>errors++,()=>{});
        var window=new Window { Content=panel,Width=360,Height=210,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.ToolWindow };
        try
        {
            window.Show();
            await Task.Delay(70);
            var button=FindButtons(panel).First(entry=>entry.ToolTip?.ToString()=="GlassDock fixture modified");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) { RoutedEvent=UIElement.MouseEnterEvent });
            // Invoke only our own fixture, then detach exactly as a real application's
            // foreground activation closes the control center.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(button.Parent is Panel parent) parent.Children.Remove(button);
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) { RoutedEvent=UIElement.MouseLeaveEvent });
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("Detached icon callbacks are harmless while the tray panel is still alive",errors==0 && PresentationSource.FromVisual(button) is null,new { errors,button.IsLoaded });
            window.Close();
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) { RoutedEvent=UIElement.MouseLeaveEvent });
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("Tray panel click, close and detached MouseLeave do not read a dead PresentationSource",errors==0,new { errors,button.IsLoaded,connected=PresentationSource.FromVisual(button) is not null });
        }
        finally { window.Close(); panel.Dispose(); }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is Button button) yield return button;
            foreach(var descendant in FindButtons(child)) yield return descendant;
        }
    }

    private static async Task VerifyRegisteredAsync(TrayHostService host,nint fixture,List<Callback> callbacks,Action<string,bool,object?> check,Action<object> external)
    {
        var snapshot=host.Snapshot;
        var own=snapshot.Where(icon=>icon.WindowHandle==fixture && Ids.Contains(icon.IconId)).OrderBy(icon=>icon.IconId).ToArray();
        check("Actual Shell registrations decoded",own.Length==3,new { count=own.Length,versions=own.Select(icon=>icon.Version).ToArray(),ids=own.Select(icon=>icon.IconId).ToArray(),host=host.Diagnostics });
        check("Native icon pixels copied",own.All(icon=>icon.Image is not null),null);
        check("Public modify reached receiver",own[1].Tooltip=="GlassDock fixture modified",null);
        check("Version preservation",own.Select(icon=>icon.Version).SequenceEqual(new uint[]{0,3,4}),null);
        var foreign=snapshot.Where(icon=>GetWindowThreadProcessId(icon.WindowHandle,out uint process)!=0 && process!=Environment.ProcessId).ToArray();
        external(new { count=foreign.Length,withImage=foreign.Count(icon=>icon.Image is not null),withCallback=foreign.Count(icon=>icon.CallbackMessage>=0x400),hidden=foreign.Count(icon=>icon.Hidden),processes=foreign.Select(icon=>ProcessName(icon.WindowHandle)).Distinct().OrderBy(name=>name).ToArray(),icons=foreign.Select(icon=>new { process=ProcessName(icon.WindowHandle),icon.IconId,icon.Guid,icon.CallbackMessage,icon.Version,icon.Hidden }),receiver=host.Diagnostics });

        host.UpdatePlacement(own[2].Key,new Rect(160,180,32,32));
        var identifier=new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=fixture,Id=Ids[2] };
        int result=Shell_NotifyIconGetRect(ref identifier,out var bounds);
        check("Shell_NotifyIconGetRect returns custom placement",result>=0 && bounds.Left==160 && bounds.Top==180 && bounds.Right==192 && bounds.Bottom==212,new { result,bounds.Left,bounds.Top,bounds.Right,bounds.Bottom });

        var anchor=new Point(176,196);
        foreach(var icon in own)
        {
            int start=callbacks.Count;
            bool sent=host.Invoke(icon.Key,TrayClick.Primary,anchor)&host.Invoke(icon.Key,TrayClick.Secondary,anchor)&
                host.Invoke(icon.Key,TrayClick.Double,anchor)&host.Invoke(icon.Key,TrayClick.KeyboardPrimary,anchor);
            check("Owned callback messages posted v"+icon.Version,sent,null);
            await Task.Delay(80);
            var received=callbacks.Skip(start).Where(item=>item.Message==CallbackBase+(uint)Array.IndexOf(Ids,icon.IconId)).ToArray();
            uint[] expected=icon.Version>=3
                ? [0x0201,0x0202,0x400,0x0204,0x0205,0x007B,0x0203,0x0202,0x400,0x401]
                : [0x0201,0x0202,0x0204,0x0205,0x0203,0x0202,0x0202];
            uint expectedW=icon.Version>3?unchecked((uint)((196<<16)|176)):icon.IconId;
            uint expectedHigh=icon.Version>3?icon.IconId<<16:0;
            check("Callback order and packing v"+icon.Version,received.Length==expected.Length && received.Select(item=>unchecked((uint)item.LParam)).SequenceEqual(expected.Select(message=>message|expectedHigh)) && received.All(item=>unchecked((uint)item.WParam)==expectedW),new { count=received.Length,expected=expected.Length });
        }
    }

    private static string ProcessName(nint window)
    {
        GetWindowThreadProcessId(window,out uint process);
        try { using var instance=Process.GetProcessById((int)process); return instance.ProcessName; }
        catch { return "unavailable"; }
    }

    private static NotifyIconData Data(nint window,int index)=>new()
    {
        Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=window,Id=Ids[index],Flags=1|2|4,
        CallbackMessage=CallbackBase+(uint)index,Icon=LoadIcon(0,32516),Tip="GlassDock fixture "+index,Info="",InfoTitle=""
    };

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id,Flags,CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message,ref NotifyIconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier,out NativeRect rectangle);
    [DllImport("user32.dll",EntryPoint="LoadIconW")] private static extern nint LoadIcon(nint instance,nint resource);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint processId);
}
