using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDock.Shell;

namespace GlassDock;

public static class Program
{
    private static Mutex? _singleInstance;
    internal static DockWindow? Dock;
    internal static SidebarWindow? Sidebar;
    internal static Island.IslandWindow? Island;
    internal static Preferences Prefs = new();
    internal static TaskbarLease Lease = new();
    internal static TrayHostService? TrayHost;
    internal static AppTrayIcon? TrayIcon;
    internal static TrayMenuSession? TrayMenus;
    internal static DesktopOrganization.DesktopBoxManager? DesktopBoxes;
    internal static DesktopResourceCoordinator? DesktopResources;
    private static ContextMenuDismissal? _menuDismissal;
    private static Services.UiResponsivenessMonitor? _responsiveness;
    internal static bool Preview;
    internal static bool CapturePreview;
    internal static bool CaptureDockPreview;
    [STAThread]
    public static int Main(string[] args)
    {
        if (TaskbarLease.RunWatchdogIfRequested(args)) return 0;
        if(args.Length==1 && args[0]=="--network-check")return Services.NetworkControlProbe.Run();
        if(args.Length==1 && args[0]=="--media-sync-check")return Music.MediaSynchronizationProbe.Run(false);
        if(args.Length==1 && args[0]=="--media-sync-live")return Music.MediaSynchronizationProbe.Run(true);
        if(args.Length>=1 && args[0]=="--media-session-check")return Music.MediaSynchronizationProbe.InspectSessions(args.Skip(1).ToArray());
        if(args.Length==2 && args[0]=="--compile-glass-shaders") { Rendering.GlassRenderer.CompileShaderAssets(args[1]); return 0; }
        if(args.Length==1 && args[0]=="--tray-snapshot") { Console.Write(System.Text.Json.JsonSerializer.Serialize(NativeTrayCatalog.CaptureSnapshot())); return 0; }
        if(args.Length==1 && args[0]=="--diagnose-shell") { Console.Write(System.Text.Json.JsonSerializer.Serialize(TrayCompatibilityProfile.Capture(),new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); return 0; }
        if(args.Length==2 && args[0]=="--lease-probe")
        {
            using var probe=new TaskbarLease();
            object before=TaskbarLease.InspectTaskbar(); probe.Enable(); Thread.Sleep(1000);
            File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new { processId=Environment.ProcessId,before,during=TaskbarLease.InspectTaskbar() }));
            Thread.Sleep(15000); return 0;
        }
        if(args.Length==2 && args[0]=="--inspect-taskbar") { File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(TaskbarLease.InspectTaskbar())); return 0; }
        if(args.Length==2 && args[0]=="--inspect-native-start") { File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(StartMenuPlacement.CaptureNativeStart(),new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); return 0; }
        if(args.Length==2 && args[0]=="--control-diagnostics") return Services.ControlDiagnostics.Run(args[1]);
        if(args.Length==2 && args[0]=="--background-diagnostics") return Rendering.BackgroundDiagnostics.Run(args[1]);
        if(args.Length==2 && args[0]=="--tray-host-probe") return TrayHostProbe.Run(args[1]);
        if(args.Length==2 && args[0]=="--responsiveness-probe") return Services.ResponsivenessProbe.Run(args[1]);
        if(args.Length==2 && args[0]=="--sidebar-tray-probe") return SidebarTrayProbe.Run(args[1]);
        if(args.Length==2 && args[0]=="--settings-preview") return SettingsPreview.Run(args[1]);
        if(args.Length==2 && args[0]=="--glass-boundary-probe") return Rendering.GlassRenderer.RunShadowBoundaryProbe(args[1]);
        if(args.Length==2 && args[0]=="--box-flow-probe") return DesktopOrganization.DesktopBoxFlowProbe.Run(args[1]);
        if(args.Length==2 && args[0]=="--dock-layer-probe") return Docking.DockLayerProbe.Run(args[1]);
        if(args.Length==2 && args[0]=="--desktop-resource-probe") return Docking.DockLayerProbe.Run(args[1],true);
        if(args.Length==2 && args[0]=="--dock-layer-fixture") return Docking.DockLayerProbe.RunFixture(args[1]);
        if(args.Length==2 && args[0]=="--desktop-box-probe") return DesktopOrganization.DesktopBoxProbe.Run(args[1]);
        if(args.Length==1 && args[0]=="--enable-startup") { var startup=Services.StartupService.SetEnabled(true); Console.Write(System.Text.Json.JsonSerializer.Serialize(startup)); return startup.Enabled?0:1; }
        if(args.Length==1 && args[0]=="--disable-startup") { var startup=Services.StartupService.SetEnabled(false); Console.Write(System.Text.Json.JsonSerializer.Serialize(startup)); return startup.Registered?1:0; }
        if(args.Length==1 && args[0]=="--startup-status") { Console.Write(System.Text.Json.JsonSerializer.Serialize(Services.StartupService.Read())); return 0; }
        if (args.Contains("--restore-taskbar"))
        {
            TaskbarLease.EmergencyRestore(); return 0;
        }
        if (args.Contains("--self-test")) return SelfTest.Run();
        _singleInstance = new Mutex(true, "Local\\GlassDock.UI", out bool first);
        if (!first) return 0;
        bool captureTray=args.Contains("--capture-tray");
        CapturePreview=args.Contains("--capture-preview") || args.Contains("--capture-controls") || args.Contains("--capture-motion") || args.Contains("--capture-sidebar-motion") || args.Contains("--capture-interactions") || captureTray;
        CaptureDockPreview=args.Contains("--capture-preview") || args.Contains("--capture-motion") || args.Contains("--capture-interactions");
        Preview=args.Contains("--preview") || CapturePreview && !captureTray;
        Prefs=Preferences.Load();
        if(!Preview)Services.StartupService.MigrateOwnedRegistration();
        var app = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        Styles.GlassMenuSurface.Install(app);
        if(!Preview) TrayMenus=new TrayMenuSession(app.Dispatcher);
        _menuDismissal=new ContextMenuDismissal(app.Dispatcher);
        app.DispatcherUnhandledException += (_,e) => { Log(e.Exception.ToString()); StartMenuPlacement.Cancel(); Lease.Disable(); e.Handled=true; Notify("操作失败，已恢复 Windows 任务栏。\n" + e.Exception.Message); };
        AppDomain.CurrentDomain.UnhandledException += (_,e) => { Log(e.ExceptionObject.ToString() ?? "Fatal error"); };
        app.Exit += (_,_) => StopModules();
        try
        {
            TaskbarLease.RestoreAbandonedLeases();
            Dock = new DockWindow(); Sidebar=new SidebarWindow();
            app.MainWindow=Dock; Dock.Show(); Sidebar.Show();

            // 启动灵动岛屿
            if (!Preview && Prefs.ShowIsland)
            {
                Island = new Island.IslandWindow();
                Island.Show();
            }

            if (!Preview && Prefs.ReplaceTaskbar) Lease.Enable();
            if(!Preview)
            {
                TrayHost=new TrayHostService(app.Dispatcher); TrayHost.Start();
                Sidebar.UpdateTrayHostBounds();
                TrayIcon=new AppTrayIcon(OpenSettings,()=>Sidebar?.OpenPanel(ControlSection.Overview),Quit);
                DesktopBoxes=new DesktopOrganization.DesktopBoxManager(); DesktopBoxes.Start();
                DesktopResources=new DesktopResourceCoordinator(Sidebar,DesktopBoxes); DesktopResources.Update(true);
            }
            if(args.Contains("--settings")) OpenSettings();
            if(args.Contains("--start-placement-ui")) StartPlacementProbe.Show();
            if(args.Contains("--controls")) Sidebar.OpenPanel(ControlSection.Overview);
            if(args.Contains("--capture-preview")) ScheduleVisualCapture();
            if(args.Contains("--capture-controls")) ScheduleControlsCapture();
            if(args.Contains("--capture-motion")) ScheduleMotionCapture();
            if(args.Contains("--capture-sidebar-motion")) ScheduleSidebarMotionCapture();
            if(args.Contains("--capture-interactions")) ScheduleInteractionCapture();
            if(captureTray) ScheduleTrayCapture();
            if(args.Contains("--record-health")) ScheduleHealthRecord();
            if(!Preview)_responsiveness=new Services.UiResponsivenessMonitor(app.Dispatcher);
            return app.Run();
        }
        catch(Exception e) { Log(e.ToString()); StopModules(); MessageBox.Show(e.Message, Branding.Name); return 1; }
    }
    internal static void ApplyPreferences()
    {
        using var activity=GlassDock.Services.UiResponsivenessMonitor.TrackActivity("apply preferences");
        PreferencesPersistence.Schedule(Prefs.Validated());
        if (!Preview && Prefs.ReplaceTaskbar) {if(!Lease.IsEnabled)Lease.Enable();} else if(Lease.IsEnabled)Lease.Disable();

        // 灵动岛屿开关
        if (!Preview && Prefs.ShowIsland)
        {
            if (Island == null)
            {
                Island = new Island.IslandWindow();
                Island.Show();
            }
        }
        else if (Island != null)
        {
            Island.Close();
            Island = null;
        }

        Dock?.ApplyPreferences(); Sidebar?.ApplyPreferences(); DesktopBoxes?.ApplyPreferences(); Island?.ApplyPreferences();
        DesktopResources?.Update(true);
        foreach(var settings in Application.Current.Windows.OfType<SettingsWindow>())settings.ApplyPreferences();
    }
    internal static void OpenSettings()
    {
        var existing=Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if(existing is not null) { existing.Activate(); return; }
        new SettingsWindow().Show();
    }
    internal static void Quit() { StartMenuPlacement.Cancel(); Lease.Disable(); Application.Current.Shutdown(); }
    private static void StopModules()
    {
        try {PreferencesPersistence.Flush();} catch(Exception exception) {Log("Preferences save: "+exception.Message);}
        DesktopResources=null;
        try { StartMenuPlacement.Cancel(); } catch(Exception exception) { Log("Start placement shutdown: "+exception); }
        foreach(var resource in new IDisposable?[]{_responsiveness,TrayMenus,_menuDismissal,DesktopBoxes,TrayIcon,TrayHost,Lease,_singleInstance})
            try { resource?.Dispose(); } catch(Exception exception) { Log("Module shutdown: "+exception); }
    }
    private static void ScheduleHealthRecord()
    {
        int samples=0;
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) };
        timer.Tick+=(_,_)=> {
            if(++samples>=15)timer.Stop();
            try
            {
                var report=new {
                    timestamp=DateTimeOffset.Now,processId=Environment.ProcessId,
                    memory=new { managedHeapBytes=GC.GetTotalMemory(false),precompiledShaders=Rendering.GlassRenderer.UsingPrecompiledShaders,graphics=Rendering.SharedDesktopCapture.InspectMemory() },
                    desktopResources=DesktopResources?.Inspect(),
                    preferences=Prefs,host=TrayHost?.Diagnostics,ownIcon=TrayIcon?.Inspect(),
                    iconCache=Services.IconService.InspectCache(),
                    startup=Services.StartupService.Read(),boxes=DesktopBoxes?.Inspect(),
                    desktopLayers=Application.Current.Windows.OfType<DesktopOrganization.DesktopBoxWindow>().Select(window=>new { window.Title,atDesktopLayer=DesktopNative.IsAtDesktopLayer(window),window.Topmost }).ToArray(),
                    dock=Dock?.InspectLayout(),sidebar=Sidebar?.InspectLayout(),
                    materials=Application.Current.Windows.OfType<GlassWindow>().Select(window=>window.InspectMaterialState()).ToArray(),
                    menus=new { open=_menuDismissal?.OpenMenuCount,hooks=_menuDismissal?.HookCount },
                    start=StartMenuPlacement.Inspect(),taskbar=TaskbarLease.InspectTaskbar()
                };
                Directory.CreateDirectory(Preferences.DataDirectory);
                File.WriteAllText(Path.Combine(Preferences.DataDirectory,"runtime-health.json"),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
            }
            catch(Exception exception) { Log("Runtime health: "+exception); }
        }; timer.Start();
    }
    private static void ScheduleVisualCapture()
    {
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) }; int stage=0;
        string directory=System.IO.Path.Combine(AppContext.BaseDirectory,"previews"); Directory.CreateDirectory(directory);
        timer.Tick+=(_,_)=> {
            if(stage++==0) { SaveVisual(Dock!,System.IO.Path.Combine(directory,"dock.png")); Dock!.PreviewMagnification(); Sidebar!.RevealForCapture(); }
            else { SaveVisual(Dock!,System.IO.Path.Combine(directory,"dock-magnified.png")); SaveVisual(Sidebar!,System.IO.Path.Combine(directory,"sidebar.png")); timer.Stop(); Quit(); }
        }; timer.Start();
    }
    private static void SaveVisual(Window window,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*1.5),(int)Math.Ceiling(window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path); encoder.Save(stream);
    }
    private static void ScheduleControlsCapture()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"previews"); Directory.CreateDirectory(directory);
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) }; int stage=0;
        timer.Tick+=(_,_)=> {
            var panel=Application.Current.Windows.OfType<QuickControlsWindow>().FirstOrDefault();
            switch(stage++)
            {
                case 0: Sidebar!.OpenPanel(ControlSection.Overview); break;
                case 1:
                    if(panel is not null) SaveVisual(panel,Path.Combine(directory,"controls.png"));
                    if(panel is not null) File.WriteAllText(Path.Combine(directory,"material-health.json"),System.Text.Json.JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,cards=panel.GlassCardCount,opticalCards=panel.OpticalGlassCardCount,dockTopmost=Dock!.Topmost,dockVisible=Dock.DesktopVisible,dockAtDesktopLayer=DesktopNative.IsAtDesktopLayer(Dock),taskbar=TaskbarLease.InspectTaskbar() },new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
                    SaveVisual(Sidebar!,Path.Combine(directory,"sidebar.png"));
                    panel?.ShowSection(ControlSection.Wifi); break;
                case 2:
                    if(panel is not null) SaveVisual(panel,Path.Combine(directory,"wifi.png"));
                    panel?.ShowSection(ControlSection.Calendar); break;
                default:
                    if(panel is not null) SaveVisual(panel,Path.Combine(directory,"calendar.png"));
                    timer.Stop(); Quit(); break;
            }
        }; timer.Start();
    }
    private static void ScheduleTrayCapture()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"previews"); Directory.CreateDirectory(directory);
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) }; bool opened=false;
        timer.Tick+=(_,_)=> {
            if(!opened) { opened=true; Sidebar!.OpenPanel(ControlSection.Apps); foreach(var window in Application.Current.Windows.OfType<DesktopOrganization.DesktopBoxWindow>()) window.RevealForCapture(); return; }
            var panel=Application.Current.Windows.OfType<QuickControlsWindow>().FirstOrDefault();
            if(panel is not null) SaveVisual(panel,Path.Combine(directory,"tray.png"));
            var icons=TrayHost?.Snapshot??[];
            var box=Application.Current.Windows.OfType<DesktopOrganization.DesktopBoxWindow>().FirstOrDefault();
            if(box is not null) SaveVisual(box,Path.Combine(directory,"desktop-box.png"));
            SaveVisual(Sidebar!,Path.Combine(directory,"sidebar.png"));
            foreach(var desktopBox in Application.Current.Windows.OfType<DesktopOrganization.DesktopBoxWindow>()) SaveVisual(desktopBox,Path.Combine(directory,"desktop-box-"+desktopBox.GetHashCode()+".png"));
            File.WriteAllText(Path.Combine(directory,"tray-health.json"),System.Text.Json.JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,host=TrayHost?.Diagnostics,ownIcon=TrayIcon?.Inspect(),startup=Services.StartupService.Read(),boxes=DesktopBoxes?.Inspect(),boxLayouts=Application.Current.Windows.OfType<DesktopOrganization.DesktopBoxWindow>().Select(window=>window.InspectLayout()).ToArray(),sidebar=Sidebar?.InspectLayout(),dock=Dock?.InspectLayout(),icons=icons.Select(icon=>new { application=Path.GetFileNameWithoutExtension(icon.OwnerPath),icon.CallbackMessage,icon.Version,icon.Hidden,hasImage=icon.Image is not null }),taskbar=TaskbarLease.InspectTaskbar() },new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
            timer.Stop(); Quit();
        }; timer.Start();
    }
    private static void ScheduleMotionCapture()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"previews"); Directory.CreateDirectory(directory);
        var states=new List<object>();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) }; int stage=0;
        timer.Tick+=(_,_)=> {
            switch(stage++)
            {
                case 0: states.Add(new { stage="idle",layout=Dock!.InspectLayout() }); SaveVisual(Dock,Path.Combine(directory,"dock-idle.png")); Dock.PreviewDragInsertion(.2); break;
                case 1: states.Add(new { stage="approach",layout=Dock!.InspectLayout() }); SaveVisual(Dock,Path.Combine(directory,"dock-approach.png")); Dock.PreviewDragInsertion(1); break;
                case 2: states.Add(new { stage="insert",layout=Dock!.InspectLayout() }); SaveVisual(Dock,Path.Combine(directory,"dock-insert.png")); Dock.PreviewEndDrag(); break;
                default:
                    states.Add(new { stage="restored",layout=Dock!.InspectLayout() }); SaveVisual(Dock,Path.Combine(directory,"dock-restored.png"));
                    File.WriteAllText(Path.Combine(directory,"motion-health.json"),System.Text.Json.JsonSerializer.Serialize(states,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
                    timer.Stop(); Quit(); break;
            }
        }; timer.Start();
    }
    private static void ScheduleSidebarMotionCapture()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"previews"); Directory.CreateDirectory(directory);
        var states=new List<object>();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) }; int stage=0;
        timer.Tick+=(_,_)=> {
            switch(stage++)
            {
                case 0: Sidebar!.RevealForCapture(); break;
                case 1: states.Add(new { stage="idle",layout=Sidebar!.InspectLayout() }); SaveVisual(Sidebar,Path.Combine(directory,"sidebar-idle.png")); Sidebar.PreviewMagnetism(true); break;
                case 2: states.Add(new { stage="hover",layout=Sidebar!.InspectLayout() }); SaveVisual(Sidebar,Path.Combine(directory,"sidebar-hover.png")); Sidebar.PreviewMagnetism(false); break;
                default:
                    states.Add(new { stage="restored",layout=Sidebar!.InspectLayout() }); SaveVisual(Sidebar,Path.Combine(directory,"sidebar-restored.png"));
                    File.WriteAllText(Path.Combine(directory,"sidebar-motion-health.json"),System.Text.Json.JsonSerializer.Serialize(states,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
                    timer.Stop(); Quit(); break;
            }
        }; timer.Start();
    }
    private static void ScheduleInteractionCapture()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"previews","interactions"); Directory.CreateDirectory(directory);
        var states=new List<object>();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1.5) }; int stage=0;
        void Record(string name)
        {
            states.Add(new { stage=name,dock=Dock!.InspectLayout(),sidebar=Sidebar!.InspectLayout() });
            SaveVisual(Dock,Path.Combine(directory,name+"-dock.png")); SaveVisual(Sidebar,Path.Combine(directory,name+"-sidebar.png"));
        }
        timer.Tick+=(_,_)=> {
            switch(stage++)
            {
                case 0: Sidebar!.RevealForCapture(); break;
                case 1: Record("idle"); Dock!.PreviewPointer(0); break;
                case 2: Record("dock-left"); Dock!.PreviewPointer(.5); break;
                case 3: Record("dock-middle"); Dock!.PreviewPointer(1); break;
                case 4: Record("dock-right"); Dock!.PreviewPointer(null); Sidebar!.PreviewPointer(.1); break;
                case 5: Record("sidebar-top"); Sidebar!.PreviewPointer(.5); break;
                case 6: Record("sidebar-middle"); Sidebar!.PreviewPointer(.9); break;
                case 7: Record("sidebar-bottom"); Sidebar!.PreviewPointer(null); break;
                case 8: Record("restored"); Sidebar!.OpenPanel(ControlSection.Overview); break;
                default:
                    Record("panel");
                    var panel=Application.Current.Windows.OfType<QuickControlsWindow>().FirstOrDefault();
                    if(panel is not null) SaveVisual(panel,Path.Combine(directory,"panel.png"));
                    File.WriteAllText(Path.Combine(directory,"report.json"),System.Text.Json.JsonSerializer.Serialize(states,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
                    timer.Stop(); Quit(); break;
            }
        }; timer.Start();
    }
    internal static void Notify(string message) { Log(message); MessageBox.Show(message,Branding.Name,MessageBoxButton.OK,MessageBoxImage.Information); }
    internal static void Log(string message)
    {
        try { Directory.CreateDirectory(Preferences.DataDirectory); var path=Path.Combine(Preferences.DataDirectory,"diagnostics.log"); if(File.Exists(path) && new FileInfo(path).Length>1_000_000) File.WriteAllText(path,""); File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine); } catch { }
    }
}
