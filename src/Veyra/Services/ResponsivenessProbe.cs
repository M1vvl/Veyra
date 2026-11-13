using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDock.Shell;

namespace GlassDock.Services;

/// <summary>Owned fixtures only: no taskbar, user configuration, audio volume or app mutations.</summary>
internal static class ResponsivenessProbe
{
    internal static int Run(string reportPath)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var results=new List<object>(); string? failure=null;
        using var release=new ManualResetEventSlim();
        Dispatcher? fixtureDispatcher=null;
        using var ready=new ManualResetEventSlim();
        nint fixture=0;
        var thread=new Thread(()=>
        {
            fixtureDispatcher=Dispatcher.CurrentDispatcher;
            using var source=new HwndSource(new HwndSourceParameters("Veyra owned slow-message fixture") {Width=1,Height=1,WindowStyle=0});
            source.AddHook((nint hwnd,int message,nint wp,nint lp,ref bool handled)=>
            {
                if(message==0x8321) { Thread.Sleep(1000); handled=true; return 17; }
                if(message==0x8322) { handled=true; return 23; }
                return 0;
            });
            fixture=source.Handle; ready.Set(); Dispatcher.Run();
        }) {IsBackground=true,Name="Veyra slow-message fixture"};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        void Check(string name,bool pass,object? detail=null)
        {
            results.Add(new {name,pass,detail});
            if(!pass)throw new InvalidOperationException(name);
        }
        app.Dispatcher.BeginInvoke(new Action(async()=>
        {
            var heartbeat=new DispatcherTimer(DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(10)};
            int ticks=0; heartbeat.Tick+=(_,_)=>ticks++; heartbeat.Start();
            try
            {
                Check("Owned slow-message fixture ready",await Task.Run(()=>ready.Wait(3000)));
                int workers=0;
                var single=new SingleFlightOperation<int>();
                var blocked=single.StartOrJoin(()=> {Interlocked.Increment(ref workers); release.Wait(); return 42;});
                for(int i=0;i<2000;i++)CheckJoin();
                void CheckJoin() {if(!ReferenceEquals(blocked,single.StartOrJoin(()=>-1)))throw new InvalidOperationException("Duplicate native worker");}
                int before=ticks; await Task.Delay(350);
                Check("Blocked native discovery keeps dispatcher responsive",ticks-before>=8,new {heartbeats=ticks-before});
                Check("Repeated refresh requests keep exactly one worker",workers==1,workers);
                release.Set(); Check("Blocked discovery publishes its eventual result",await blocked==42);
                Check("Worker can refresh again after completion",await single.StartOrJoin(()=>84)==84);
                using(var dockRelease=new ManualResetEventSlim())
                {
                    Program.Preview=true; Program.Prefs=new Preferences {GlassEnabled=false};
                    int dockScans=0;
                    var discovery=new WindowDiscovery(_=> {Interlocked.Increment(ref dockScans); dockRelease.Wait(); return new WindowDiscoverySnapshot([],[]);});
                    var dock=new DockWindow(discovery);
                    try
                    {
                        dock.Show(); int dockBefore=ticks;
                        await Task.Delay(400);
                        Check("Production Dock remains interactive during blocked Shell scan",ticks-dockBefore>=10 && dockScans==1,new {heartbeats=ticks-dockBefore,dockScans});
                    }
                    finally {dockRelease.Set(); dock.Close();}
                    await Task.Delay(60);
                    Check("Dock safely discards completion after window closes",!dock.IsVisible);
                }
                var gate=new ExplorerForwardingGate(); var watch=Stopwatch.StartNew();
                bool sent=gate.TryForward(fixture,0x8321,0,0,out _);
                Check("Foreign unresponsive WndProc has bounded wait",!sent && watch.ElapsedMilliseconds<400,watch.ElapsedMilliseconds);
                watch.Restart();
                for(int i=0;i<2000;i++)gate.TryForward(fixture,0x8321,0,0,out _);
                Check("Notification burst does not repeat foreign waits",watch.ElapsedMilliseconds<250,watch.ElapsedMilliseconds);
                await Task.Delay(2100);
                Check("Explorer forwarding recovers after cooldown",gate.TryForward(fixture,0x8322,0,0,out var value) && value==23);
                var snapshot=await new WindowDiscovery().ReadAsync([]).WaitAsync(TimeSpan.FromSeconds(12));
                Check("Production discovery excludes owned windows",snapshot.Windows.All(item=>item.ProcessId!=Environment.ProcessId),new {windows=snapshot.Windows.Count,running=snapshot.Running.Count});
                var events=new ConcurrentQueue<string>();
                using(var monitor=new UiResponsivenessMonitor(app.Dispatcher,events.Enqueue,80,15))
                {
                    await Task.Delay(80);
                    // This pause belongs only to the isolated diagnostic process.
                    Thread.Sleep(230);
                    await Task.Delay(100);
                    Check("Lightweight monitor records stall and recovery",events.Any(item=>item.StartsWith("UI stall")) && events.Any(item=>item.StartsWith("UI responsiveness")),events.ToArray());
                }
                var audio=await AudioService.ReadBackgroundAsync().WaitAsync(TimeSpan.FromSeconds(6));
                Check("Audio background read completes without changing volume",audio is not null,new {audio!.Available,audio.Volume,audio.Muted});
            }
            catch(Exception ex) {failure=ex.ToString();}
            finally
            {
                release.Set(); heartbeat.Stop(); fixtureDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
                string path=Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,JsonSerializer.Serialize(new {timestamp=DateTimeOffset.Now,results,failure},new JsonSerializerOptions {WriteIndented=true}));
                app.Shutdown(failure is null?0:1);
            }
        }));
        return app.Run();
    }
}
