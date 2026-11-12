using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassDock.Docking;

/// <summary>Runs the production layer policy against an owned, separate-process window.</summary>
internal static class DockLayerProbe
{
    internal static int Run(string reportPath,bool resourceOnly=false)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var results = new List<object>();
        string directory = Path.Combine(Path.GetTempPath(), "GlassDock-layer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "parent.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId }));
        if(!resourceOnly)File.WriteAllText(Path.Combine(directory,"pin-colors"),"owned optical fixture");
        ProbeGlassWindow? dock = null;
        DockLayerService? layer = null;
        Process? fixture = null;
        DesktopOrganization.DesktopBoxManager? boxes = null;
        string? failure = null;
        int commandSequence = 0;

        void Check(string name, bool pass, object? detail = null)
        {
            results.Add(new { name, pass, detail });
            if (!pass) throw new InvalidOperationException(name);
        }

        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                dock = new ProbeGlassWindow();
                dock.Show();
                layer = new DockLayerService(dock);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"Veyra.dll"));
                start.ArgumentList.Add("--dock-layer-fixture");
                start.ArgumentList.Add(directory);
                fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the owned layer fixture.");
                AllowSetForegroundWindow((uint)fixture.Id);

                async Task<FixtureState> Command(string action)
                {
                    int sequence = ++commandSequence;
                    string temporary = Path.Combine(directory, "command.tmp");
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new FixtureCommand(sequence, action)));
                    File.Move(temporary, Path.Combine(directory, "command.json"), true);
                    FixtureState? state = null;
                    await WaitUntilAsync(() =>
                    {
                        string statePath = Path.Combine(directory, "state.json");
                        if (!File.Exists(statePath)) return false;
                        state = JsonSerializer.Deserialize<FixtureState>(ReadShared(statePath));
                        return state?.Sequence == sequence;
                    }, 5000, "Owned fixture did not acknowledge " + action);
                    if (action != "close")
                    {
                        for (int attempt = 0; attempt < 6 && GetForegroundWindow() != (nint)state!.Window; attempt++)
                        {
                            AllowSetForegroundWindow((uint)fixture.Id);
                            ShowWindow((nint)state!.Window,IsIconic((nint)state.Window)?9:5);
                            SetForegroundWindow((nint)state!.Window);
                            await Task.Delay(100);
                        }
                    }
                    await Task.Delay(120);
                    return state!;
                }

                object LayerDetail(nint expectedFixture, nint foregroundBefore, bool overlay)
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint actualForegroundProcess);
                    return new
                    {
                        expectedFixture = expectedFixture.ToInt64(),
                        fixtureProcess = fixture.Id,
                        foregroundBefore = foregroundBefore.ToInt64(),
                        foregroundAfter = GetForegroundWindow().ToInt64(),
                        actualForegroundProcess,
                        dock = dock.Handle.ToInt64(),
                        dock.Topmost,
                        dockAboveFixture = IsAbove(dock.Handle, expectedFixture),
                        fixtureAboveDock = IsAbove(expectedFixture, dock.Handle),
                        overlay,
                        policy = layer.Inspect()
                    };
                }

                var normal = await Command("normal");
                var fixtureHandle = (nint)normal.Window;
                await WaitUntilAsync(() => GetForegroundWindow() == fixtureHandle, 20000,
                    "Windows did not grant foreground activation to the owned fixture.");
                if(!resourceOnly)
                {
                var foregroundBefore = GetForegroundWindow();
                bool floating = layer.Update();
                await Task.Delay(120);
                Check("Restored application remains foreground beneath the floating Dock", floating && dock.Topmost && GetForegroundWindow() == fixtureHandle && IsAbove(dock.Handle, fixtureHandle), LayerDetail(fixtureHandle, foregroundBefore, floating));
                bool affinityRead = GetWindowDisplayAffinity(dock.Handle, out uint affinity);
                Check("Dock remains visible to external screen capture", affinityRead && affinity == 0, new { affinityRead, affinity });

                await WaitUntilAsync(() => dock.HasBlueBackground, 6000, "The Dock did not refract the blue owned window beneath it.");
                dock.SaveMaterial(Path.Combine(directory, "over-blue-window.png"));
                var blue = dock.ReadCenter();
                Check("GPU glass contains the real blue window below the capture-visible Dock", blue.Blue > blue.Red + 40, blue);
                var hiddenOverlay=await Command("overlay"); layer.Update(); await Task.Delay(600);
                Check("Zero-opacity full-screen native overlay does not cover wallpaper or restored windows",hiddenOverlay.OverlayWindow!=0
                    && !Rendering.NativeWindowComposition.Read((nint)hiddenOverlay.OverlayWindow).Visible
                    && !Rendering.SharedDesktopCapture.SamplesWindow((nint)hiddenOverlay.OverlayWindow) && dock.HasBlueBackground,
                    new {hiddenOverlay.OverlayWindow,composition=Rendering.NativeWindowComposition.Read((nint)hiddenOverlay.OverlayWindow),sampled=Rendering.SharedDesktopCapture.SamplesWindow((nint)hiddenOverlay.OverlayWindow),pixel=dock.ReadCenter()});
                await Command("overlay-off"); layer.Update();

                int blackFrames=0;
                for(int cycle=0;cycle<3;cycle++)
                {
                    var oldMaterial=dock.MaterialIdentity;
                    dock.RestartMaterialCapture();
                    long deadline=Environment.TickCount64+6000;
                    do
                    {
                        await Task.Delay(16);
                        var sample=dock.ReadCenter();
                        if(sample.Red<30 && sample.Green<30 && sample.Blue<30)blackFrames++;
                    } while((ReferenceEquals(oldMaterial,dock.MaterialIdentity) || !dock.HasBlueBackground) && Environment.TickCount64<deadline);
                    if(ReferenceEquals(oldMaterial,dock.MaterialIdentity) || !dock.HasBlueBackground)throw new TimeoutException("A repeated capture restart did not recover the real blue background");
                }
                Check("Repeated capture restarts retain real glass without publishing black startup frames",blackFrames==0,new {cycles=3,blackFrames,pixel=dock.ReadCenter()});

                dock.ShowCaptureMarker(); await Task.Delay(150);
                string captureRequestTemporary=Path.Combine(directory,"capture-request.tmp");
                File.WriteAllText(captureRequestTemporary,JsonSerializer.Serialize(new Rendering.CaptureReadbackProbe.Request(dock.Handle.ToInt64(),160,50)));
                File.Move(captureRequestTemporary,Path.Combine(directory,"capture-request.json"),true);
                await WaitUntilAsync(()=>File.Exists(Path.Combine(directory,"capture-result.json")),6500,"External capture fixture did not return its screen/window samples.");
                var external=JsonSerializer.Deserialize<Rendering.CaptureReadbackProbe.Result>(File.ReadAllText(Path.Combine(directory,"capture-result.json")))!;
                Check("Independent DXGI screen recording captures the visible glass content",external.Screen is {Red:>180,Green:<80,Blue:>180} && external.Error is null,external);
                Check("Independent Windows window capture includes the visible glass content",external.Window is {Red:>180,Green:<80,Blue:>180} && external.Error is null,external);
                await Task.Delay(250);
                Check("Internal refraction does not feed its own captured content back into glass",dock.HasBlueBackground,dock.ReadCenter());
                dock.HideCaptureMarker();

                var moved = await Command("move");
                floating = layer.Update();
                await WaitUntilAsync(() => dock.HasRedBackground, 6000, "Moving the owned window did not update the refracted background.");
                dock.SaveMaterial(Path.Combine(directory, "over-red-window.png"));
                var red = dock.ReadCenter();
                Check("Moving the owned two-color window changes the real GPU background", floating && red.Red > red.Blue + 40 && GetForegroundWindow() == (nint)moved.Window, red);

                var maximized = await Command("maximize");
                foregroundBefore = GetForegroundWindow();
                floating = layer.Update();
                await Task.Delay(120);
                Check("Maximized application covers the Dock", !floating && !dock.Topmost && IsAbove((nint)maximized.Window, dock.Handle), LayerDetail((nint)maximized.Window, foregroundBefore, floating));

                var restored = await Command("normal");
                foregroundBefore = GetForegroundWindow();
                floating = layer.Update();
                await Task.Delay(120);
                Check("Restoring the application returns the Dock above its window", floating && dock.Topmost && IsAbove(dock.Handle, (nint)restored.Window), LayerDetail((nint)restored.Window, foregroundBefore, floating));

                var fullscreen = await Command("fullscreen");
                foregroundBefore = GetForegroundWindow();
                floating = layer.Update();
                await Task.Delay(120);
                Check("Borderless full-screen application covers the Dock", !floating && !dock.Topmost && IsAbove((nint)fullscreen.Window, dock.Handle), LayerDetail((nint)fullscreen.Window, foregroundBefore, floating));

                var finalNormal = await Command("normal");
                foregroundBefore = GetForegroundWindow();
                floating = layer.Update();
                await Task.Delay(120);
                Check("Leaving full screen restores the floating Dock without activating it", floating && dock.Topmost && GetForegroundWindow() == (nint)finalNormal.Window, LayerDetail((nint)finalNormal.Window, foregroundBefore, floating));
                }

                // Exercise automatic detection with the real components, separate from the optical probe.
                layer.Dispose(); layer=null; dock.Close(); dock=null;
                Program.Prefs=Preferences.Load(); Program.Preview=true;
                boxes=new DesktopOrganization.DesktopBoxManager(Path.Combine(directory,"resource-boxes.json"));
                boxes.Configuration.PresetVersion=DesktopOrganization.DesktopBoxPresets.CurrentVersion;
                boxes.Configuration.Boxes.Clear(); boxes.CreateBox("资源回归"); boxes.Configuration.GroupPinned=true;
                Program.DesktopBoxes=boxes;
                Program.Dock=new DockWindow(); Program.Dock.Show();
                Program.Sidebar=new SidebarWindow(); Program.Sidebar.Show();
                boxes.Start(); Program.DesktopResources=new DesktopResourceCoordinator(Program.Sidebar,boxes);
                await Command("normal");
                await WaitUntilAsync(()=>!Program.Dock.DesktopSuspended && !Program.Sidebar.DesktopSuspended && boxes.VisibleBoxCount==1 && Program.Dock.HasMaterialResources,6000,"Restored fixture did not resume real desktop components.");
                object Snapshot(string stage)
                {
                    using var process=Process.GetCurrentProcess();
                    return new {stage,processId=Environment.ProcessId,workingSetBytes=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,managedHeapBytes=GC.GetTotalMemory(false),graphics=Rendering.SharedDesktopCapture.InspectMemory(),policy=Program.DesktopResources!.Inspect(),dock=Program.Dock!.InspectLayout(),sidebar=Program.Sidebar!.InspectLayout(),boxes=boxes.Inspect()};
                }
                Check("Automatic resource policy keeps restored-window glass active",!Program.Dock.DesktopSuspended && !Program.Sidebar.DesktopSuspended && !boxes.DesktopSuspended,Snapshot("restored"));
                var realBox=boxes.DesktopWindows.Single(); var originalBoxBounds=realBox.VisibleBoundsPixels;
                var dockMonitor=DesktopNative.MonitorRect(Program.Dock);
                realBox.ApplyDragBounds(new Rect((dockMonitor.Left+dockMonitor.Right)/2-160,dockMonitor.Bottom-340,originalBoxBounds.Width,originalBoxBounds.Height)); realBox.UpdateDesktopLayer();
                await WaitUntilAsync(()=>realBox.MaterialCenterColor.B>realBox.MaterialCenterColor.R+40,6000,"Box glass did not refract the blue restored window below it");
                Check("Mapping box floats above a restored window and refracts its actual pixels",realBox.Topmost && IsAbove(new WindowInteropHelper(realBox).Handle,fixtureHandle),realBox.InspectMaterialState());
                Check("A missing foreground resolves the visible restored window without a desktop click",DesktopNative.ResolveForegroundForDesktopPolicy(0)==fixtureHandle);
                var shells=application.Windows.OfType<GlassWindow>().Where(window=>window.IsVisible).ToArray();
                foreach(var shell in shells)ShowWindow(new WindowInteropHelper(shell).Handle,0);
                Program.Dock.PreviewResourceSuspension(false); Program.DesktopResources.Update();
                await WaitUntilAsync(()=>DesktopNative.IsWindowVisible(new WindowInteropHelper(Program.Dock).Handle) && DesktopNative.IsWindowVisible(new WindowInteropHelper(Program.Sidebar).Handle) && DesktopNative.IsWindowVisible(new WindowInteropHelper(realBox).Handle),3000,"Native shell-hidden surfaces did not recover without input");
                Check("Shell-hidden Dock, sidebar and boxes recover without mouse input",GetForegroundWindow()==fixtureHandle);
                await Command("maximize");
                await WaitUntilAsync(()=>Program.Dock.DesktopSuspended && Program.Sidebar.DesktopSuspended && boxes.DesktopSuspended,6000,"Maximized fixture did not suspend desktop components.");
                await Task.Delay(1200);
                Check("Maximized window automatically releases real desktop rendering resources",Rendering.SharedDesktopCapture.ActiveOutputCount==0 && !Program.Dock.HasMaterialResources && !Program.Sidebar.HasMaterialResources && boxes.DesktopWindows.All(box=>!box.HasMaterialResources),Snapshot("maximized"));
                await Command("fullscreen"); await Task.Delay(600);
                Check("Borderless fullscreen retains the resource suspension",Program.Dock.DesktopSuspended && Program.Sidebar.DesktopSuspended && boxes.DesktopSuspended && Rendering.SharedDesktopCapture.ActiveOutputCount==0,Snapshot("fullscreen"));
                await Command("normal");
                await WaitUntilAsync(()=>!Program.Dock.DesktopSuspended && !Program.Sidebar.DesktopSuspended && !boxes.DesktopSuspended && Program.Dock.HasMaterialResources && boxes.VisibleBoxCount==1,6000,"Leaving fullscreen did not resume real desktop components.");
                Check("Leaving fullscreen restores real components without stealing focus",GetForegroundWindow()==fixtureHandle && Rendering.SharedDesktopCapture.ActiveOutputCount>0,Snapshot("returned"));
                await Command("close");
            }
            catch (Exception exception)
            {
                failure=exception.ToString();
                if(dock is not null)
                {
                    dock.SaveMaterial(Path.Combine(directory,"failed-material.png"));
                    results.Add(new { name="Failure diagnostics",pass=false,detail=new {pixel=dock.ReadCenter(),graphics=Rendering.SharedDesktopCapture.InspectMemory(),layer=layer?.Inspect(),dock.Left,dock.Top,dock.Width,dock.Height} });
                }
            }
            finally
            {
                layer?.Dispose();
                dock?.Close();
                Program.DesktopResources=null; boxes?.Dispose(); Program.Dock?.Close(); Program.Sidebar?.Close();
                if (fixture is not null)
                {
                    if (!fixture.HasExited && !fixture.WaitForExit(1000)) fixture.Kill();
                    fixture.Dispose();
                }
                string fullPath = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string report = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, mode = "owned separate-process fixture windows only", artifacts = directory, results, failure }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fullPath, report);
                Console.WriteLine(report);
                application.Shutdown(failure is null ? 0 : 1);
            }
        }));
        return application.Run();
    }

    internal static int RunFixture(string directory)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var colors = new Grid();
        colors.ColumnDefinitions.Add(new ColumnDefinition());
        colors.ColumnDefinitions.Add(new ColumnDefinition());
        colors.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(25, 72, 215)) });
        var red = new Border { Background = new SolidColorBrush(Color.FromRgb(215, 48, 25)) };
        Grid.SetColumn(red, 1);
        colors.Children.Add(red);
        // Keep the disposable color surface visible above ambient tool overlays.
        // The tested Dock still has to position itself above this real window.
        var window = new Window { Title = "GlassDock owned layering fixture", Content = colors, Width = 1000, Height = 480, ShowInTaskbar = false, ShowActivated = true,Topmost=File.Exists(Path.Combine(directory,"pin-colors")) };
        window.Show();
        var monitor = DesktopNative.MonitorRect(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        double center = (monitor.Left + monitor.Right) / (2 * dpi.DpiScaleX);
        double top = monitor.Bottom / dpi.DpiScaleY - 480;
        int lastSequence = -1;
        int activationRetries = 0;
        bool stayMinimized=false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var lifetime = Stopwatch.StartNew();
        nint hiddenOverlay=0;
        timer.Tick += (_, _) =>
        {
            if (lifetime.Elapsed > TimeSpan.FromSeconds(45)) { timer.Stop(); application.Shutdown(); return; }
            var ownHandle = new WindowInteropHelper(window).Handle;
            string captureRequest=Path.Combine(directory,"capture-request.json"),captureResult=Path.Combine(directory,"capture-result.json");
            if(File.Exists(captureRequest) && !File.Exists(captureResult))
            {
                var request=JsonSerializer.Deserialize<Rendering.CaptureReadbackProbe.Request>(File.ReadAllText(captureRequest))!;
                string captureTemporary=captureResult+".tmp"; File.WriteAllText(captureTemporary,JsonSerializer.Serialize(Rendering.CaptureReadbackProbe.Read(request))); File.Move(captureTemporary,captureResult,true);
            }
            if(IsIconic(ownHandle) && !stayMinimized) { ShowWindow(ownHandle,9); ActivateOwnedFixture(ownHandle); }
            if (activationRetries > 0 && GetForegroundWindow() != ownHandle)
            {
                activationRetries--;
                ShowWindow(ownHandle,IsIconic(ownHandle)?9:5);
                window.Activate();
                ActivateOwnedFixture(ownHandle);
            }
            string commandPath = Path.Combine(directory, "command.json");
            if (!File.Exists(commandPath)) return;
            var command = JsonSerializer.Deserialize<FixtureCommand>(ReadShared(commandPath));
            if (command is null || command.Sequence == lastSequence) return;
            lastSequence = command.Sequence;
            stayMinimized=command.Action=="minimize";
            if(stayMinimized)activationRetries=0;
            if(command.Action=="overlay")
            {
                hiddenOverlay=CreateWindowEx(0x800A8,"STATIC","GlassDock owned transparent overlay",unchecked((int)0x90000004),monitor.Left,monitor.Top,monitor.Right-monitor.Left,monitor.Bottom-monitor.Top,0,0,0,0);
                if(hiddenOverlay==0 || !SetLayeredWindowAttributes(hiddenOverlay,0,0,2))throw new InvalidOperationException("Could not create owned native overlay");
            }
            else if(command.Action=="overlay-off" && hiddenOverlay!=0) {DestroyWindow(hiddenOverlay); hiddenOverlay=0;}
            if (command.Action is "normal" or "move")
            {
                window.WindowState = WindowState.Normal;
                window.WindowStyle = WindowStyle.SingleBorderWindow;
                window.ResizeMode = ResizeMode.CanResize;
                window.Width = 1000; window.Height = 480;
                window.Left = center - (command.Action == "move" ? 740 : 260);
                window.Top = top;
            }
            else if (command.Action == "maximize") window.WindowState = WindowState.Maximized;
            else if (command.Action == "minimize") window.WindowState = WindowState.Minimized;
            else if (command.Action == "fullscreen")
            {
                window.WindowState = WindowState.Normal;
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.Left = monitor.Left / dpi.DpiScaleX;
                window.Top = monitor.Top / dpi.DpiScaleY;
                window.Width = (monitor.Right - monitor.Left) / dpi.DpiScaleX;
                window.Height = (monitor.Bottom - monitor.Top) / dpi.DpiScaleY;
            }
            if (command.Action is not ("close" or "minimize"))
            {
                activationRetries = 6;
                ShowWindow(ownHandle,IsIconic(ownHandle)?9:5);
                window.Activate();
                ActivateOwnedFixture(ownHandle);
            }
            string state = JsonSerializer.Serialize(new FixtureState(command.Sequence, new WindowInteropHelper(window).Handle.ToInt64(), window.WindowState.ToString(),hiddenOverlay.ToInt64()));
            string temporary = Path.Combine(directory, "state.tmp");
            File.WriteAllText(temporary, state);
            File.Move(temporary, Path.Combine(directory, "state.json"), true);
            if (command.Action == "close") { timer.Stop(); window.Close(); application.Shutdown(); }
        };
        timer.Start();
        window.Closed += (_, _) => application.Shutdown();
        return application.Run();
    }

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMilliseconds, string reason)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!ready())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(reason);
            await Task.Delay(30);
        }
    }

    private static string ReadShared(string path)
    {
        // The peer atomically replaces this file. Keep delete sharing enabled so
        // observing a result cannot make its next replacement fail on Windows.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(stream); return reader.ReadToEnd();
    }

    // Used only by our disposable fixture. No synthesized keyboard/mouse input.
    private static void ActivateOwnedFixture(nint window)
    {
        uint own=GetCurrentThreadId(),foregroundThread=GetWindowThreadProcessId(GetForegroundWindow(),out _);
        bool attached=foregroundThread!=0 && foregroundThread!=own && AttachThreadInput(own,foregroundThread,true);
        try { SetForegroundWindow(window); }
        finally { if(attached)AttachThreadInput(own,foregroundThread,false); }
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window,int index,int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(int extendedStyle,string className,string title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first,uint second,bool attach);

    private static bool IsAbove(nint above, nint below)
    {
        nint current = GetTopWindow(0);
        for (int count = 0; current != 0 && count < 4096; count++, current = GetWindow(current, 2))
        {
            if (current == above) return true;
            if (current == below) return false;
        }
        return false;
    }

    private sealed record FixtureCommand(int Sequence, string Action);
    private sealed record FixtureState(int Sequence, long Window, string State,long OverlayWindow=0);
    private sealed record Pixel(byte Red, byte Green, byte Blue);

    private sealed class ProbeGlassWindow : GlassWindow
    {
        private readonly Border _captureMarker=new() {Width=20,Height=20,Background=Brushes.Magenta,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Hidden};
        internal ProbeGlassWindow() : base("GlassDock owned optical layer probe")
        {
            Width = 320; Height = 100; Topmost = false;
            ContentLayer.Children.Add(_captureMarker);
            Loaded += (_, _) => Reposition();
        }
        internal nint Handle => new WindowInteropHelper(this).Handle;
        internal object? MaterialIdentity=>Glass.Source;
        internal void RestartMaterialCapture()=>ReleaseMaterial();
        internal void ShowCaptureMarker()=>_captureMarker.Visibility=Visibility.Visible;
        internal void HideCaptureMarker()=>_captureMarker.Visibility=Visibility.Hidden;
        internal bool HasBlueBackground => ReadCenter() is { } pixel && pixel.Blue > pixel.Red + 40;
        internal bool HasRedBackground => ReadCenter() is { } pixel && pixel.Red > pixel.Blue + 40;
        internal Pixel ReadCenter()
        {
            if (Glass.Source is not BitmapSource image) return new Pixel(0, 0, 0);
            byte[] pixel = new byte[4];
            image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
            return new Pixel(pixel[2], pixel[1], pixel[0]);
        }
        internal void SaveMaterial(string path)
        {
            if (Glass.Source is not BitmapSource image) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        protected override void Reposition()
        {
            if (!IsLoaded) return;
            var monitor = DesktopNative.MonitorRect(this); var dpi = VisualTreeHelper.GetDpi(this);
            Left = (monitor.Left + monitor.Right) / (2 * dpi.DpiScaleX) - Width / 2;
            // Avoid the running user's real Dock capture-exclusion rectangle.
            Top = monitor.Bottom / dpi.DpiScaleY - 360;
            SetSurface(new Rect(8, 8, Width - 16, Height - 16), 24);
            RenderGlass();
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
