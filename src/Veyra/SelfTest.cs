using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Models;
using GlassDock.Rendering;
using GlassDock.Services;
using GlassDock.Shell;

namespace GlassDock;

internal static class SelfTest
{
    public static int Run()
    {
        string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"GlassDock-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results=new List<object>();
        void Test(string name,Action action) { try { action(); results.Add(new { name,pass=true,error="" }); } catch(Exception ex) { results.Add(new { name,pass=false,error=ex.ToString() }); } }
        void Assert(bool value,string reason) { if(!value) throw new InvalidOperationException(reason); }
        Test("Sidebar modes retain legacy preferences and persist fullscreen topmost",()=> {
            var legacy=JsonSerializer.Deserialize<Preferences>("{\"SidebarPinned\":true}")!.Validated();
            Assert(legacy.SidebarMode==SidebarDisplayMode.Fixed,"Legacy fixed mode changed");
            legacy.SidebarMode=SidebarDisplayMode.AlwaysOnTop;
            var restored=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(legacy))!.Validated();
            Assert(restored.SidebarAlwaysOnTop && restored.SidebarPinned,"Always-on-top mode was not persisted");
            restored.SidebarMode=SidebarDisplayMode.AutoHide;
            Assert(!restored.SidebarPinned && !restored.SidebarAlwaysOnTop,"Auto-hide retained a topmost override");
        });
        Test("Actual shipped HLSL VS/PS compile + cbuffer",GlassRenderer.ValidateShader);
        Test("Lyric clock stays monotonic through quantization, jitter and a transient zero",()=> {
            var clock=new Island.IslandPlaybackClock();
            clock.Observe(10,true,0);
            int revision=clock.Revision;
            double previous=10;
            for(int i=1;i<=320;i++)
            {
                double now=i*.0125;
                if(i%2==0)
                {
                    double sample=10+Math.Floor(now*10)/10+(i%4==0?.035:-.035);
                    clock.Observe(i==80 || i==82?0:sample,true,now);
                }
                double current=clock.Read(now);
                Assert(current>=previous,"A normal playback sample reversed the lyric clock");
                Assert(current-previous<.025,"An ordinary sample made the lyric clock leap");
                previous=current;
            }
            Assert(clock.Revision==revision,"A brief zero sample was mistaken for a seek");
            Assert(Math.Abs(clock.Read(4)-14)<.2,"Jitter filtering lost synchronization");
            Assert(clock.Read(7)==clock.Read(8),"Missing timeline data advances forever");
        });
        Test("Lyric clock confirms real seeks and holds pause without snapping",()=> {
            var clock=new Island.IslandPlaybackClock();
            clock.Observe(10,true,0);
            int revision=clock.Revision;
            clock.Observe(40,true,.1);
            clock.Observe(40.01,true,.11);
            clock.Observe(40.025,true,.125);
            Assert(clock.Revision==revision,"A seek was accepted before transient samples were excluded");
            clock.Observe(40.075,true,.175);
            Assert(clock.Revision==revision+1 && Math.Abs(clock.Read(.175)-40.075)<.0001,
                "A confirmed external seek did not resynchronize");
            clock.Observe(5,false,.2,true);
            Assert(clock.Read(1)==5,"Explicit backward seek or pause drifted");
            clock.Observe(5,true,1);
            Assert(clock.Read(1.1)>5,"Resume did not restart the lyric clock");
            clock.Reset();
            Assert(clock.Read(2)==0,"A new track retained the previous clock");
        });
        Test("Compact word lyrics paginate without horizontal transforms",()=> {
            var view=new Island.IslandCompactLyrics();
            view.Measure(new Size(130,42));view.Arrange(new Rect(0,0,130,42));
            var line=new LyricLine {Time=TimeSpan.FromSeconds(10),Text=new string('字',60)};
            view.ShowLine(line,16,10);
            string first=new System.Windows.Documents.TextRange(view.ContentStart,view.ContentEnd).Text;
            view.ShowLine(line,16,15);
            string last=new System.Windows.Documents.TextRange(view.ContentStart,view.ContentEnd).Text;
            Assert(first.Length<line.Text.Length && last.Length<line.Text.Length,"Long lyrics were not segmented");
            Assert(view.RenderTransform.Value.IsIdentity,"Compact lyrics retained a sliding transform");
            Assert(view.ToolTip?.ToString()?.Contains("估算")==true,"Estimated word timings were not labeled");
            line=new LyricLine {Time=TimeSpan.FromSeconds(10),Text="甲乙",Words=new() {
                new LyricWord {Text="甲",Time=TimeSpan.FromSeconds(10),Duration=TimeSpan.FromSeconds(1)},
                new LyricWord {Text="乙",Time=TimeSpan.FromSeconds(12),Duration=TimeSpan.FromSeconds(1)}}};
            view.ShowLine(line,14,12);
            Assert(view.ToolTip?.ToString()=="逐字歌词","Available word timings were discarded");
            var yrc=Music.LyricsParser.ParseYRC("[10000,2000](10000,1000,0)甲(11000,1000,0)乙");
            Assert(yrc.Count==1 && yrc[0].Words?.Count==2 && yrc[0].Words![0].Text=="甲"
                && yrc[0].Words![0].Time==TimeSpan.FromSeconds(10) && yrc[0].Words![1].Text=="乙",
                "YRC prefix timings shifted words or dropped the final word");
        });
        Test("Visible glass refreshes captures across desktop and fullscreen presentation",()=> {
            var state=new CapturePresentationState();
            Assert(!state.Observe(1,false,0,0,2560,1440),"Initial desktop unnecessarily reset capture");
            Assert(state.Observe(2,true,0,0,2560,1440),"Entering fullscreen retained the desktop session");
            for(int i=0;i<100;i++)
                Assert(!state.Observe(2,true,0,0,2560,1440),"Stable fullscreen repeatedly recreated captures");
            Assert(state.Observe(1,false,0,0,2560,1440),"Returning to desktop retained the fullscreen session");
            Assert(!state.Observe(3,false,100,100,800,600),"A restored window unnecessarily reset capture");
            Assert(state.Observe(3,true,0,0,2560,1440),"Same-window fullscreen entry was missed");
            Assert(state.Observe(4,true,0,0,2560,1440),"Switching fullscreen applications retained old captures");
            Assert(state.Observe(4,false,100,100,800,600),"Restoring a fullscreen window was missed");
        });
        Test("Capture presentation ignores invalid foreground observations without losing recovery",()=> {
            var state=new CapturePresentationState();
            state.Observe(1,true,0,0,1920,1080);
            Assert(!state.Observe(0,false,0,0,0,0),"A transient missing foreground reset capture");
            Assert(!state.Observe(2,false,0,0,0,0),"An empty window changed presentation state");
            Assert(state.Observe(2,false,0,0,1920,1080),"Desktop recovery was lost after an invalid foreground");
        });
        Test("Window capture shutdown never waits for DWM on the dispatcher",()=> {
            var capture=SharedDesktopCapture.Acquire(new Int32Rect(0,0,100,100));
            try
            {
                using var fixture=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Veyra capture shutdown fixture") {Width=16,Height=16,WindowStyle=unchecked((int)0x90000000)});
                var source=new WindowCaptureSource(fixture.Handle,capture.Device);
                var clock=System.Diagnostics.Stopwatch.StartNew();
                source.Dispose();
                Assert(clock.ElapsedMilliseconds<100,"Capture shutdown blocked its caller");
                source.Dispose();
            }
            finally {capture.Release();}
        });
        Test("Native overlay opacity and color key remain separate from captured pixels",()=> {
            var hidden=NativeWindowComposition.FromAttributes(0x80080,true,0,0,2);
            Assert(!hidden.Visible && !hidden.CanOcclude,"A zero-opacity native overlay can hide the underlying desktop");
            var half=NativeWindowComposition.FromAttributes(0x80000,true,0,128,2);
            Assert(Math.Abs(half.Opacity-128/255f)<.0001 && !half.CanOcclude,"Layered window opacity was discarded");
            var perPixel=NativeWindowComposition.FromAttributes(0x80000,false,0,0,0);
            Assert(perPixel.Visible && perPixel.Opacity==1,"Per-pixel alpha was confused with global alpha zero");
            Assert(NativeWindowComposition.FromAttributes(0x80000,true,0x00010203,0,1).UsesColorKey,"Native color key is missing");
        });
        Test("Background waits for complete ready coverage before replacing retained glass",()=> {
            var monitor=new Int32Rect(0,0,2560,1440);
            var regions=new[]{new Int32Rect(100,100,320,120),new Int32Rect(2450,500,110,400)};
            Assert(!WindowBackgroundScene.CoversRegions(regions,Array.Empty<Int32Rect>(),monitor),"An empty capture was accepted as a black background");
            Assert(!WindowBackgroundScene.CoversRegions(regions,new[]{regions[0]},monitor),"A pending sidebar source replaced its retained material");
            Assert(WindowBackgroundScene.CoversRegions(regions,new[]{monitor},monitor),"A ready desktop did not cover all glass surfaces");
            Assert(WindowBackgroundScene.CoversRegions(regions,new[]{regions[0],regions[1]},monitor),"Separate ready HWNDs did not combine their coverage");
            Assert(WindowBackgroundScene.CoversRegions(new[]{new Int32Rect(-30,100,120,80)},new[]{new Int32Rect(0,100,90,80)},monitor),"Offscreen padding incorrectly blocked valid capture");
        });
        Test("An empty native background scene never publishes its cleared render target",()=> {
            var capture=SharedDesktopCapture.Acquire(new Int32Rect(100,100,16,16));
            try
            {
                // No real HWND occupies this offscreen test coordinate. Production
                // capture used to report its opaque black clear as a completed frame.
                var outside=new Int32Rect(1000000,1000000,16,16);
                using var scene=new WindowBackgroundScene(capture.Device,capture.Context,outside);
                scene.SetRegion(scene,outside);
                Assert(!scene.Update() && scene.SourceCount==0,"Empty scene published a synthetic black frame");
                scene.ResetSources();
                Assert(!scene.Update(),"Capture reset published black before any native frame arrived");
            }
            finally {capture.Release();}
        });
        Test("GPU SDF geometry, exact flat normals, edge symmetry and finite optical paths",GlassRenderer.ValidateOpticalGeometry);
        Test("Native WLAN struct sizes and field offsets",WifiService.ValidateLayouts);
        Test("Native tray registration and wire protocol layouts",()=> { AppTrayIcon.ValidateContracts(); TrayProtocol.ValidateLayouts(); });
        Test("WPF file data and Windows drag-image helper COM interop",Docking.ExternalDragVisual.ValidateInterop);
        Test("Visible-edge spacing preserves an 8 DIP minimum and DPI-scaled nearest slots",()=> {
            var screen=new Rect(8,8,2544,1424); var control=new Rect(2358,6,120,34);
            var gap=new Size(8,8);
            var controlGap=new Size(4,4);
            var area=DesktopOrganization.DesktopBoxSpacing.ChildArea(screen,control,new Size(1420,438),controlGap,"Top");
            Assert(area.Top==44,"Total organizer to branch gap is not 4 DIP");
            var reservedControl=control; reservedControl.Inflate(-4,-4);
            var other=new Rect(1284,56,596,438);
            Assert(DesktopOrganization.DesktopBoxSpacing.TryPlace(new Rect(1888,41,596,372),new[]{reservedControl,other},area,gap,out var placed),"No nearby clear branch slot was found");
            Assert(Math.Abs(placed.Left-other.Right-8)<.1 && placed.Top==44,"Independent horizontal and vertical spacing constraints were not combined");
            Assert(DesktopOrganization.DesktopBoxSpacing.Separated(placed,other,gap) && DesktopOrganization.DesktopBoxSpacing.Separated(placed,control,controlGap),"Placed branch violates the minimum gap");
            var scaled=new Size(12,12);
            Assert(DesktopOrganization.DesktopBoxSpacing.TryPlace(new Rect(336,84,330,168),new[]{new Rect(0,84,330,168)},new Rect(0,84,1920,900),scaled,out var highDpi) && highDpi.Left==342,"150% DPI spacing is not 12 physical pixels");
        });
        Test("Volume waves use thirds, mute and exact boundary states",()=> {
            Assert(Sidebar.VolumeLevelIcon.Bands(.3,false)==1 && Sidebar.VolumeLevelIcon.Bands(1d/3,false)==1,"Lower third lights more than one wave");
            Assert(Sidebar.VolumeLevelIcon.Bands(1d/3+.000001,false)==2 && Sidebar.VolumeLevelIcon.Bands(2d/3,false)==2,"Middle third boundary is incorrect");
            Assert(Sidebar.VolumeLevelIcon.Bands(2d/3+.000001,false)==3 && Sidebar.VolumeLevelIcon.Bands(1,false)==3,"Upper third does not light three waves");
            Assert(Sidebar.VolumeLevelIcon.Bands((float)(1d/3),false)==1 && Sidebar.VolumeLevelIcon.Bands((float)(2d/3),false)==2,"Native float32 endpoint rounding changes an exact third");
            Assert(Sidebar.VolumeLevelIcon.Bands(.8,true)==0 && Sidebar.VolumeLevelIcon.Bands(0,false)==0 && Sidebar.VolumeLevelIcon.Bands(.8,false,false)==0,"Silent or unavailable endpoint incorrectly lights waves");
        });
        Test("Sidebar real layout magnifies neighbors, preserves ordering and contracts",()=> {
            var stack=new StackPanel { Width=74 };
            for(int index=0;index<9;index++) stack.Children.Add(new Button { Width=42,Height=42 });
            var magnetic=new Sidebar.SidebarMagnetism(stack);
            magnetic.RefreshLayout(); // Exercise the initial not-yet-arranged layout.
            stack.Measure(new Size(74,double.PositiveInfinity)); stack.Arrange(new Rect(0,0,74,stack.DesiredSize.Height)); stack.UpdateLayout();
            magnetic.PointAt(4.5*42);
            for(int frame=0;frame<100;frame++) magnetic.Update(1d/60,false,.68);
            Assert(((FrameworkElement)stack.Children[4]).RenderTransform.Value.M11>=1.199,"Hovered native control did not reach the 1.2 limit");
            Assert(((FrameworkElement)stack.Children[3]).RenderTransform.Value.M11>1.1,"Neighbor did not participate");
            Assert(stack.Children.Cast<FrameworkElement>().All(child=>child.RenderTransform.Value.M11<=1.2),"Sidebar exceeds the 1.2 magnification limit");
            double previous=double.NegativeInfinity;
            foreach(FrameworkElement child in stack.Children)
            {
                var bounds=child.TransformToAncestor(stack).TransformBounds(new Rect(0,0,child.ActualWidth,child.ActualHeight));
                Assert(bounds.Top>=previous-.05,"Magnetic controls overlap"); previous=bounds.Bottom;
            }
            magnetic.Leave(); for(int frame=0;frame<100;frame++) magnetic.Update(1d/60,false,.68);
            Assert(magnetic.Expansion==0 && stack.Children.Cast<FrameworkElement>().All(child=>child.RenderTransform.Value.M11==1),"Sidebar did not fully contract");
            magnetic.PointAt(4.5*42); magnetic.Update(1d/60,true,.68);
            Assert(magnetic.Expansion==0,"Reduced motion still magnifies");
        });
        Test("Sidebar pull requires a deliberate left gesture and cancels without mode changes",()=> {
            var pull=new Sidebar.SidebarPullGesture();
            pull.Begin(new Point(100,100)); pull.Update(new Point(81,105)); Assert(pull.End().Toggle,"Short left pull was not recognized");
            Assert(!pull.End().Toggle,"A gesture toggles twice");
            pull.Begin(new Point(100,100)); pull.Update(new Point(149,100)); Assert(!pull.End().Toggle,"Rightward drag toggled");
            pull.Begin(new Point(100,100)); pull.Update(new Point(49,180)); Assert(!pull.End().Toggle,"Vertical drag toggled");
            pull.Begin(new Point(100,100)); pull.Update(new Point(30,100)); Assert(!pull.End(true).Toggle,"Cancelled drag toggled");
            pull.Begin(new Point(100,100)); pull.Update(new Point(83,100)); Assert(!pull.End().Toggle,"Too-short pull toggled");
            pull.Begin(new Point(100,100)); pull.Update(new Point(81,100)); pull.Update(new Point(84,100)); Assert(pull.End().Toggle,"Release jitter lost a deliberate pull");
        });
        Test("Desktop boxes preserve source paths and original file bytes",()=> {
            string source=System.IO.Path.Combine(directory,"桌面原文件.txt");
            File.WriteAllText(source,"Original desktop content"); byte[] original=File.ReadAllBytes(source);
            string configuration=System.IO.Path.Combine(directory,"desktop-boxes.json");
            using(var boxes=new DesktopOrganization.DesktopBoxManager(configuration))
            {
                var box=boxes.Configuration.Boxes[0];
                boxes.AddReferencesAsync(box,new[]{source,source}).GetAwaiter().GetResult();
                Assert(box.Items.Count==1 && box.Items[0].Path==source,"References were duplicated or relocated");
            }
            var store=new DesktopOrganization.DesktopBoxStore(configuration);
            var restored=store.Load(); Assert(restored.Boxes[0].Items[0].Path==source,"Source path changed during persistence");
            Assert(File.Exists(source) && File.ReadAllBytes(source).SequenceEqual(original),"Original desktop file changed");
            File.WriteAllText(configuration,"{invalid");
            var invalid=new DesktopOrganization.DesktopBoxStore(configuration); invalid.Load();
            invalid.SaveAsync(restored).GetAwaiter().GetResult();
            Assert(File.ReadAllText(configuration)=="{invalid","Invalid configuration was overwritten");
        });
        Test("Desktop presets group original references and fit separate visible boxes",()=> {
            string desktop=System.IO.Path.Combine(directory,"preset-desktop"); Directory.CreateDirectory(desktop);
            string application=System.IO.Path.Combine(desktop,"App.url"),document=System.IO.Path.Combine(desktop,"笔记.txt");
            File.WriteAllText(application,"[InternetShortcut]\nURL=https://example.com\n"); File.WriteAllText(document,"unchanged");
            Directory.CreateDirectory(System.IO.Path.Combine(desktop,"folder.exe"));
            File.WriteAllText(System.IO.Path.Combine(desktop,"desktop.ini"),"ignored");
            var scan=DesktopOrganization.DesktopBoxPresets.Scan(new[]{desktop,desktop});
            var boxes=DesktopOrganization.DesktopBoxPresets.Create(scan.Items);
            var available=new Rect(100,20,780,600); DesktopOrganization.DesktopBoxPresets.Arrange(boxes,available);
            Assert(scan.Items.Count==3 && boxes.Count==2,"Preset scan duplicated paths or included desktop.ini");
            Assert(boxes.All(box=>box.Enabled && box.Pinned && !box.Collapsed),"First preset is not visible and pinned");
            Assert(boxes[0].Items.Count==1 && boxes[1].Items.Count==2,"A directory ending with .exe was treated as an application");
            var first=new Rect(boxes[0].Left,boxes[0].Top,boxes[0].Width,boxes[0].Height);
            var second=new Rect(boxes[1].Left,boxes[1].Top,boxes[1].Width,boxes[1].Height);
            Assert(available.Contains(first) && available.Contains(second) && !first.IntersectsWith(second),"Preset boxes overlap or leave available screen area");
            Assert(File.ReadAllText(document)=="unchanged" && File.Exists(application),"Preset scan changed a source file");
        });
        Test("Mapping boxes create beyond two, preserve independent names and move only references",()=> {
            string configuration=Path.Combine(directory,"many-boxes.json"),source=Path.Combine(directory,"mapped.txt");
            File.WriteAllText(source,"preserved mapping source");
            using(var boxes=new DesktopOrganization.DesktopBoxManager(configuration))
            {
                boxes.Configuration.Boxes.Clear();
                for(int i=0;i<18;i++) boxes.CreateBox("独立盒 "+i);
                Assert(boxes.Configuration.Boxes.Count==18,"New mapping boxes are limited or missing");
                var first=boxes.Configuration.Boxes[0]; var second=boxes.Configuration.Boxes[1];
                boxes.AddReferencesAsync(first,new[]{source}).GetAwaiter().GetResult();
                Assert(boxes.MoveReference(second,first.Id,first.Items[0].Id),"Cross-box reference move failed");
                Assert(first.Items.Count==0 && second.Items.Single().Path==source,"Cross-box move relocated a file");
                boxes.RemoveReference(second,second.Items[0]);
                Assert(File.ReadAllText(source)=="preserved mapping source","Removing a mapping changed the source");
            }
            var restored=new DesktopOrganization.DesktopBoxStore(configuration).Load();
            Assert(restored.Boxes.Count==18 && restored.Boxes.Select(box=>box.Name).Distinct().Count()==18,"Independent box names did not persist");
            Assert(restored.Boxes.All(box=>box.Kind=="Mapping" && box.AutoSize),"A non-mapping box was created");
        });
        Test("Recycle Bin uses native PIDL drag data, icons and persistent Shell references",()=> {
            var item=DesktopOrganization.DesktopShellReference.CreateRecycleBin();
            var cida=DesktopOrganization.DesktopShellReference.CreateRecycleIdList();
            var data=new DataObject(); data.SetData(DesktopOrganization.DesktopShellReference.ShellIdList,new MemoryStream(cida));
            Assert(DesktopOrganization.DesktopShellReference.ReadPaths(data).SequenceEqual(new[]{DesktopOrganization.DesktopShellReference.RecycleBin}),"Explorer PIDL drag data did not resolve the Recycle Bin");
            var broken=(byte[])cida.Clone(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(8),uint.MaxValue);
            Assert(DesktopOrganization.DesktopShellReference.ReadIdList(broken).Length==0 && DesktopOrganization.DesktopShellReference.ReadIdList(new byte[12]).Length==0,"Malformed CIDA offsets were accepted");
            var icon=Services.IconService.GetFileIconAsync(item.Path,32).GetAwaiter().GetResult();
            Assert(icon.Image.IsFrozen && icon.Image.PixelWidth==32 && icon.Image.PixelHeight==32,"Native Recycle Bin icon was not resolved at the requested size");
            byte[] pixels=new byte[32*32*4]; icon.Image.CopyPixels(pixels,32*4,0);
            Assert(Enumerable.Range(0,32*32).Any(i=>pixels[i*4+3]>0),"Recycle Bin icon is empty");
            var info=DesktopOrganization.DesktopShellReference.OpenInfo(item.Path);
            Assert(File.Exists(info.FileName) && info.ArgumentList.SequenceEqual(new[]{item.Path}),"Recycle Bin launch does not address the native Explorer folder");
            string configuration=Path.Combine(directory,"recycle-box.json");
            using(var boxes=new DesktopOrganization.DesktopBoxManager(configuration))
            {
                var box=boxes.Configuration.Boxes[0]; boxes.AddReferencesAsync(box,new[]{item.Path,"shell:RecycleBinFolder"}).GetAwaiter().GetResult();
                Assert(box.Items.Count==1 && box.Items[0].IsDirectory,"Recycle Bin alias was duplicated or rejected");
            }
            var restored=new DesktopOrganization.DesktopBoxStore(configuration).Load();
            Assert(restored.Boxes[0].Items.Single().Path==item.Path,"Shell reference was discarded during reload");
        });
        Test("Folder hierarchy remaps preserve boundaries and follow rename chains",()=> {
            var changes=new[] { new DesktopOrganization.ReferenceChange(@"C:\Desktop\项目",@"C:\Archive\资料"),new DesktopOrganization.ReferenceChange(@"C:\Archive\资料\子目录",@"C:\Archive\资料\新版子目录") };
            Assert(DesktopOrganization.DesktopReferenceMonitor.Resolve(@"C:\Desktop\项目\子目录\文档.txt",changes)==@"C:\Archive\资料\新版子目录\文档.txt","Nested references were flattened or lost during a parent rename");
            Assert(DesktopOrganization.DesktopReferenceMonitor.Resolve(@"C:\Desktop\项目之外\文档.txt",changes)==@"C:\Desktop\项目之外\文档.txt","A similarly prefixed sibling folder was remapped");
            Assert(DesktopOrganization.DesktopReferenceMonitor.InRecycleBin(@"C:\$Recycle.Bin\S-1-5-21\$Rtest.txt") && !DesktopOrganization.DesktopReferenceMonitor.InRecycleBin(@"C:\Desktop\$Recycle.Bin\normal.txt"),"A normal nested folder was mistaken for the system recycle namespace");
        });
        Test("Rapid atomic configuration saves persist the latest complete snapshot",()=> {
            string configuration=Path.Combine(directory,"coalesced-config.json");
            var store=new DesktopOrganization.DesktopBoxStore(configuration);
            var model=new DesktopOrganization.DesktopBoxConfiguration { Boxes=[new()] }; var writes=new List<Task>();
            for(int index=0;index<80;index++) { model.Boxes[0].Name="保存批次 "+index; writes.Add(store.SaveAsync(model)); }
            Task.WhenAll(writes).GetAwaiter().GetResult();
            Assert(store.Load().Boxes.Single().Name=="保存批次 79" && store.LastError is null,"A stale queued save replaced the final configuration");
        });
        Test("Mapping insertion persists stable ordering without duplicate paths",()=> {
            string configuration=Path.Combine(directory,"ordered-boxes.json");
            var paths=Enumerable.Range(0,5).Select(i=>Path.Combine(directory,"ordered-"+i+".txt")).ToArray();
            foreach(var path in paths)File.WriteAllText(path,"unchanged source");
            using(var boxes=new DesktopOrganization.DesktopBoxManager(configuration))
            {
                var box=boxes.Configuration.Boxes[0];
                boxes.AddReferencesAsync(box,paths.Take(3)).GetAwaiter().GetResult();
                string moved=box.Items[0].Id;
                Assert(boxes.MoveReference(box,box.Id,moved,2) && box.Items.Select(item=>item.Path).SequenceEqual(new[]{paths[1],paths[2],paths[0]}),"Forward in-box insertion shifted the wrong index");
                Assert(boxes.MoveReference(box,box.Id,moved,0),"Backward in-box insertion failed");
                boxes.AddReferencesAsync(box,new[]{paths[3],paths[1],paths[4]},1).GetAwaiter().GetResult();
                Assert(box.Items.Select(item=>item.Path).SequenceEqual(new[]{paths[0],paths[3],paths[4],paths[1],paths[2]}),"Multi-file insertion lost order or duplicated an existing mapping");
            }
            var restored=new DesktopOrganization.DesktopBoxStore(configuration).Load().Boxes[0];
            Assert(restored.Items.Select(item=>item.Path).SequenceEqual(new[]{paths[0],paths[3],paths[4],paths[1],paths[2]}),"Inserted order did not survive saving");
            Assert(paths.All(path=>File.ReadAllText(path)=="unchanged source"),"Reordering changed source bytes");
        });
        Test("Mapping resize snaps to whole icon columns and rows within screen bounds",()=> {
            var available=new Size(720,720);
            Assert(DesktopOrganization.DesktopBoxLayout.ResizeSize(new Size(412+31,244+32),available)==new Size(412,244),"Sub-cell movement changed the grid");
            Assert(DesktopOrganization.DesktopBoxLayout.ResizeSize(new Size(412+40,244+40),available)==new Size(476,310),"Resize did not advance by one column and row");
            Assert(DesktopOrganization.DesktopBoxLayout.ResizeSize(new Size(0,0),available)==new Size(220,112),"Minimum icon grid was not retained");
            Assert(DesktopOrganization.DesktopBoxLayout.ResizeSize(new Size(900,900),new Size(620,400))==new Size(604,376),"Screen limit allowed a partial row or column");
        });
        Test("Adaptive mapping viewports grow, contract and retain a bounded virtualized size",()=> {
            var available=new Size(900,650);
            var small=DesktopOrganization.DesktopBoxLayout.AdaptiveSize(1,available);
            var medium=DesktopOrganization.DesktopBoxLayout.AdaptiveSize(30,available);
            var large=DesktopOrganization.DesktopBoxLayout.AdaptiveSize(500,available);
            Assert(medium.Width>small.Width && medium.Height>small.Height,"Mapping box did not grow with content");
            Assert(large.Width<=720 && large.Height<=650,"Adaptive box escaped the display viewport");
            Assert(DesktopOrganization.DesktopBoxLayout.AdaptiveSize(0,available).Height==112,"Empty box retains excess space");
        });
        Test("Migrated WitchDrawer alignment snaps visible edges, gaps and centers",()=> {
            var target=new Rect(100,100,220,112);
            var snap=DesktopOrganization.Alignment.SnapGeometry.Evaluate(new Rect(104,226,220,112),new[]{target},10,8);
            Assert(snap.SnappedVisibleLeft==100 && snap.SnappedVisibleTop==220,"Mapping box edge/gap snap failed");
            Assert(snap.VerticalGuideX==100 && snap.HorizontalGuideY==216,"Mapping alignment guides are misplaced");
            var no=DesktopOrganization.Alignment.SnapGeometry.Evaluate(new Rect(700,700,220,112),new[]{target},10,8);
            Assert(no.VerticalGuideX is null && no.HorizontalGuideY is null,"Distant boxes generate spurious guides");
        });
        Test("Control slider keyboard commands, calendar navigation and rendering",()=> {
            var layout=new StackPanel { Width=330,Background=Brushes.White };
            var slider=new UnifiedSlider { Minimum=0,Maximum=100,Value=37,SmallChange=1,LargeChange=10,Style=ControlStyles.Slider };
            var calendar=ControlStyles.Calendar();
            layout.Children.Add(slider); layout.Children.Add(calendar);
            layout.Measure(new Size(330,500)); layout.Arrange(new Rect(0,0,330,layout.DesiredSize.Height)); layout.UpdateLayout();
            Assert(slider.Template is not null && slider.ActualHeight>0,"Slider template did not materialize");
            Slider.IncreaseSmall.Execute(null,slider); Assert(Math.Abs(slider.Value-38)<.001,"Keyboard increment failed without a visible thumb");
            Slider.MinimizeValue.Execute(null,slider); Assert(slider.Value==0,"Keyboard minimum failed");
            Slider.MaximizeValue.Execute(null,slider); Assert(slider.Value==100,"Keyboard maximum failed"); slider.Value=37;
            var month=calendar.DisplayMonth;
            var controls=Descendants(calendar).OfType<Button>().ToList();
            controls.First(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="下个月").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(calendar.DisplayMonth==month.AddMonths(1),"Next-month action failed");
            controls.First(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="回到今天").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(calendar.SelectedDate==DateTime.Today && calendar.DisplayMonth==new DateTime(DateTime.Today.Year,DateTime.Today.Month,1),"Today action failed");
            layout.UpdateLayout();
            var bitmap=new RenderTargetBitmap(330,(int)Math.Ceiling(layout.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(layout); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(System.IO.Path.Combine(directory,"control-templates.png")); encoder.Save(stream);
        });
        Test("Native input and watchdog recovery contracts",()=> { Assert(ShellService.HasValidInputLayout,"INPUT ABI mismatch"); Assert(TaskbarLease.VerifyRecoveryContracts().Count==0,string.Join(";",TaskbarLease.VerifyRecoveryContracts())); });
        Test("Shortcut metadata, native icon, atomic order/dedup persistence",()=> {
            object? shell=null,shortcut=null;
            try
            {
                var target=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","notepad.exe");
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                dynamic wsh=shell!; shortcut=wsh.CreateShortcut(System.IO.Path.Combine(directory,"测试 应用.lnk")); dynamic link=shortcut;
                link.TargetPath=target; link.Arguments="\"C:\\a b\\example.txt\" /A"; link.WorkingDirectory=directory; link.IconLocation=Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\shell32.dll,3"); link.Save();
                var pin=PinStore.CreatePin(System.IO.Path.Combine(directory,"测试 应用.lnk"));
                Assert(string.Equals(pin.Target,target,StringComparison.OrdinalIgnoreCase),".lnk target differs"); Assert(pin.Arguments=="\"C:\\a b\\example.txt\" /A",".lnk arguments differ"); Assert(pin.WorkingDirectory==directory,"working directory differs");
                var icon=IconService.GetIcon(pin,96); Assert(icon.PixelWidth==96 && icon.PixelHeight==96,"icon dimensions differ");
                var store=new PinStore(System.IO.Path.Combine(directory,"pins.json"));
                var other=PinStore.CreatePin(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                store.Save(new[]{pin,other,pin with { Id="duplicate",Path="different.lnk" }});
                var read=store.Load(); Assert(read.Count==2 && read[0].Id==pin.Id && read[1].Id==other.Id,"ordered JSON round-trip/dedup failed");
                File.WriteAllText(store.FilePath,"{broken"); Assert(store.Load().Count==0 && store.LastLoadError is not null,"corrupt JSON not reported"); Assert(File.ReadAllText(store.FilePath)=="{broken","corrupt source overwritten");
            }
            finally { if(shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); if(shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        });
        Test("Window discovery excludes self",()=>Assert(WindowService.GetAllWindows().All(w=>w.ProcessId!=Environment.ProcessId),"own process appeared"));
        Test("Dock and sidebar share one capture; independent resize and disposal",GlassRenderer.ValidateConcurrentSurfaces);
        Test("GPU readback presents without waiting for next capture tick",GlassRenderer.ValidatePendingPresentation);
        Test("Capture interruption retains material and recovers with a fresh frame",GlassRenderer.ValidateRecoveryRetention);
        Test("Real D3D capture → mip chain → optics → regional readback",()=> {
            using var renderer=new GlassRenderer(); BitmapSource? bitmap=null;
            for(int i=0;i<12;i++) { if(renderer.TryRender(new Int32Rect(100,100,480,90),new GlassOptions(),out bitmap) && bitmap is not null) break; Thread.Sleep(30); }
            Assert(bitmap is not null,renderer.LastError??"No capture frame");
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap!));
            using(var stream=File.Create(System.IO.Path.Combine(directory,"glass-gpu.png"))) encoder.Save(stream);
            var pixels=new byte[480*90*4]; bitmap!.CopyPixels(pixels,480*4,0);
            Assert(pixels[3]<20,"outer corner must be transparent"); Assert(pixels[(45*480+240)*4+3]>240,"center mask must be opaque composed desktop");
        });
        string report=JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,artifacts=directory,results },new JsonSerializerOptions { WriteIndented=true });
        var reportPath=System.IO.Path.Combine(AppContext.BaseDirectory,"self-test.json"); File.WriteAllText(reportPath,report);
        Console.WriteLine(report); return report.Contains("\"pass\": false")?1:0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i); yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
}
