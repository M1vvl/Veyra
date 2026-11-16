using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDock.Shell;

namespace GlassDock.DesktopOrganization;

/// <summary>Bounded regression using disposable mapping data and our own windows/menus.</summary>
internal static class DesktopBoxProbe
{
    internal static int Run(string reportPath)
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        Styles.GlassMenuSurface.Install(app);
        Program.Prefs=Preferences.Load(); Program.Preview=Program.CapturePreview=Program.CaptureDockPreview=true;
        PreferencesPersistence.WritesSuspendedForProbe=true;
        using var dismissal=new ContextMenuDismissal(app.Dispatcher);
        using var trayMenus=new TrayMenuSession(app.Dispatcher); Program.TrayMenus=trayMenus;
        string directory=Path.Combine(Path.GetTempPath(),"GlassDock-boxes-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var results=new List<object>(); string? failure=null; DesktopBoxManager? manager=null;
        var watchdog=new DispatcherTimer { Interval=TimeSpan.FromSeconds(45) };
        watchdog.Tick+=(_,_)=> { failure="Owned desktop-box probe timed out"; app.Shutdown(1); };
        void Check(string name,bool pass,object? detail=null)
        {
            results.Add(new { name,pass,detail }); if(!pass) throw new InvalidOperationException(name);
        }
        async Task<bool> Until(Func<bool> condition,int milliseconds=5500)
        {
            var watch=Stopwatch.StartNew();
            while(!condition() && watch.ElapsedMilliseconds<milliseconds)await Task.Delay(50);
            return condition();
        }
        void CheckSpacing(string stage)
        {
            var boxes=manager!.DesktopWindows.ToArray(); var control=manager.Group!.ControlGlassBounds;
            var dpi=VisualTreeHelper.GetDpi(boxes[0]); var gap=new Size(DesktopBoxSpacing.Gap*dpi.DpiScaleX,DesktopBoxSpacing.Gap*dpi.DpiScaleY);
            var controlGap=new Size(DesktopBoxSpacing.ControlGap*dpi.DpiScaleX,DesktopBoxSpacing.ControlGap*dpi.DpiScaleY);
            bool clear=boxes.All(box=>DesktopBoxSpacing.Separated(box.VisibleBoundsPixels,control,controlGap));
            for(int first=0;first<boxes.Length;first++)for(int second=first+1;second<boxes.Length;second++)
                clear&=DesktopBoxSpacing.Separated(boxes[first].VisibleBoundsPixels,boxes[second].VisibleBoundsPixels,gap);
            Check("Shared minimum gaps hold after "+stage,clear,new { control,gap,children=boxes.Select(box=>box.VisibleBoundsPixels).ToArray() });
        }
        app.Dispatcher.BeginInvoke(new Action(async()=> {
            try
            {
                manager=new DesktopBoxManager(Path.Combine(directory,"mapping.json"));
                manager.Configuration.PresetVersion=DesktopBoxPresets.CurrentVersion;
                manager.Configuration.Boxes.Clear();
                var first=manager.CreateBox("工作资料"); var second=manager.CreateBox("快捷工具"); manager.CreateBox("临时归档");
                Program.DesktopBoxes=manager;
                Program.Dock=new DockWindow(); Program.Dock.Show();
                Program.Sidebar=new SidebarWindow(); Program.Sidebar.Show(); Program.Sidebar.RevealForCapture();
                manager.Start(); await Task.Delay(250);
                Check("Three independently named mapping boxes are visible",manager.VisibleBoxCount==3 && manager.Configuration.Boxes.All(box=>box.Kind=="Mapping"),manager.Inspect());
                var window=app.Windows.OfType<DesktopBoxWindow>().Single(box=>box.BoxId==first.Id);
                first.Name="项目文件"; manager.Update(first); await Task.Delay(50);
                Check("Rename updates the actual desktop title",window.Title=="uilicd · 项目文件",window.Title);
                string original=Path.Combine(directory,"原始文件.txt"); File.WriteAllText(original,"original source bytes");
                string folder=Path.Combine(directory,"原始文件夹"); Directory.CreateDirectory(folder);
                await manager.AddReferencesAsync(first,new[]{original,folder}); await Task.Delay(80);
                double smallWidth=window.Width,smallHeight=window.Height;
                var additions=new List<string>();
                for(int index=0;index<28;index++) { string file=Path.Combine(directory,"项目 "+index+".txt"); File.WriteAllText(file,"source "+index); additions.Add(file); }
                await manager.AddReferencesAsync(first,additions); await Task.Delay(350);
                Check("Adaptive desktop viewport grows for imported references",window.Width>smallWidth && window.Height>smallHeight,new { smallWidth,smallHeight,window.Width,window.Height });
                CheckSpacing("adaptive growth");
                var rowsBefore=Descendants<DesktopBoxItemRow>(window).ToArray();
                manager.Update(first); await Task.Delay(50);
                Check("Presentation-only changes retain the realized icon rows",rowsBefore.SequenceEqual(Descendants<DesktopBoxItemRow>(window)));
                var nativePixels=await Task.WhenAll(Services.IconService.GetFileIconAsync(additions[0],32),Services.IconService.GetFileIconAsync(additions[1],32));
                Check("Identical native file-type icons share frozen pixels",ReferenceEquals(nativePixels[0].Image,nativePixels[1].Image) && nativePixels.All(icon=>icon.Image.IsFrozen),Services.IconService.InspectCache());
                Check("Source file and folder remain at their exact absolute paths",File.ReadAllText(original)=="original source bytes" && Directory.Exists(folder) && first.Items.All(item=>Path.IsPathFullyQualified(item.Path)));
                Program.Prefs.ReducedMotion=false;
                var mappedOrder=first.Items.Select(item=>item.Id).ToArray(); double hoverWidth=window.Width,hoverHeight=window.Height;
                window.PreviewItemMagnetism(1); await Task.Delay(450);
                var magnified=window.InspectMagnetism();
                Check("Box uses Dock proximity and spring enlargement while keeping neighboring icons inside their cells",magnified.Single(item=>item.Id==first.Items[1].Id).Scale>1.3
                    && magnified.Count(item=>item.Scale>1.05)>=2 && magnified.All(item=>item.Contained),magnified);
                Capture(window,Path.Combine(directory,"box-magnetism.png"));
                window.PreviewItemMagnetism(0); await Task.Delay(80);
                Check("Box magnetic animation can reverse without changing mappings or grid geometry",window.MagnetismAnimating && mappedOrder.SequenceEqual(first.Items.Select(item=>item.Id)) && window.Width==hoverWidth && window.Height==hoverHeight);
                window.PreviewItemMagnetism(null); await Task.Delay(650);
                Check("Leaving the box restores exact icon size and detaches its frame callback",!window.MagnetismAnimating && window.InspectMagnetism().All(item=>item.Scale==1 && item.X==0 && item.Y==0));
                Program.Prefs.ReducedMotion=true; window.PreviewItemMagnetism(0); await Task.Delay(50);
                Check("Reduced motion disables box magnetic transforms",!window.MagnetismAnimating && window.InspectMagnetism().All(item=>item.Scale==1));
                Program.Prefs.ReducedMotion=false;
                var orderBefore=first.Items.Select(item=>item.Id).ToArray();
                var dragged=first.Items[0]; string identity=first.Id+":"+dragged.Id;
                window.PreviewItemInsertion(new[]{dragged.Path},identity,new Point(),5);
                await app.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.Loaded);
                Check("In-box dragging reserves a real slot without mutating stored order",window.DisplayItemIds[5] is null && window.DisplayItemIds.Count==orderBefore.Length && first.Items.Select(item=>item.Id).SequenceEqual(orderBefore),window.InspectLayout());
                Check("Insertion shifts existing native icon views through a spring",window.MovingItemCount>0,window.InspectLayout());
                window.PreviewItemInsertion(new[]{dragged.Path},identity,new Point(),2);
                window.ClearItemDragPreview(); await Task.Delay(300);
                Check("Cancelling insertion restores the original sequence and removes the slot",window.DisplayItemIds.SequenceEqual(orderBefore) && first.Items.Select(item=>item.Id).SequenceEqual(orderBefore));
                window.PreviewItemInsertion(new[]{dragged.Path},identity,new Point(),5);
                bool reordered=await window.CommitItemInsertionAsync(new[]{dragged.Path},identity); await Task.Delay(300);
                Check("In-box drop commits the requested index once",reordered && first.Items[5].Id==dragged.Id && first.Items.Select(item=>item.Id).Distinct().Count()==orderBefore.Length);
                // Restore the fixture's order, so later reference tests remain independent.
                manager.MoveReference(first,first.Id,dragged.Id,0);
                var target=manager.DesktopWindows.Single(box=>box.BoxId==second.Id);
                await manager.AddReferencesAsync(second,additions.Take(3)); await Task.Delay(120);
                double fullRowHeight=target.Height; var fullRowWidth=target.Width;
                bool sourceTracked=window.TrackItemDragTarget(new[]{dragged.Path},identity,new Point(100,20));
                bool passedTarget=target.TrackItemDragTarget(new[]{dragged.Path},identity,new Point(60,20));
                var passedWindow=manager.DesktopWindows.Single(box=>box.BoxId==manager.Configuration.Boxes[2].Id);
                bool passedNext=passedWindow.TrackItemDragTarget(new[]{original},null,new Point(40,20));
                Check("Passing across boxes does not leave insertion gaps or change their sizes",sourceTracked && passedTarget && passedNext
                    && window.DisplayItemIds.SequenceEqual(first.Items.Select(item=>item.Id)) && target.DisplayItemIds.All(id=>id is not null) && passedWindow.DisplayItemIds.All(id=>id is not null)
                    && target.Height==fullRowHeight && target.Width==fullRowWidth);
                manager.ClearItemDragPreviews();
                Check("Cancelling a cross-box drag clears every temporary target without mutating mappings",manager.DesktopWindows.All(box=>box.DisplayItemIds.All(id=>id is not null)) && second.Items.Count==3 && first.Items.Contains(dragged));
                target.PrepareItemDrop(new[]{original},null,new Point(),1);
                await app.Dispatcher.InvokeAsync(()=>target.UpdateLayout(),DispatcherPriority.Loaded);
                Check("Only the final drop target expands and reserves the committed icon position",(target.Height>fullRowHeight || target.Width>fullRowWidth) && second.Items.Count==3 && target.DisplayItemIds[1] is null && passedWindow.DisplayItemIds.All(id=>id is not null),new { fullRowHeight,fullRowWidth,layout=target.InspectLayout() });
                Capture(target,Path.Combine(directory,"box-insertion-motion.png"));
                await Task.Delay(350); Capture(target,Path.Combine(directory,"box-insertion-preview.png"));
                target.ClearItemDragPreview(); await Task.Delay(80);
                Check("Leaving an expanded target restores its size and references",Math.Abs(target.Height-fullRowHeight)<.1 && Math.Abs(target.Width-fullRowWidth)<.1 && second.Items.Count==3);
                Program.Prefs.ReducedMotion=true;
                target.PreviewItemInsertion(new[]{original},null,new Point(),0);
                await app.Dispatcher.InvokeAsync(()=>target.UpdateLayout(),DispatcherPriority.Loaded); await Task.Delay(70);
                Check("Reduced motion keeps insertion and growth functional without tile motion",target.DisplayItemIds[0] is null && target.MovingItemCount==0);
                target.ClearItemDragPreview(); Program.Prefs.ReducedMotion=false;
                second.AutoSize=false; manager.Update(second);
                target.PreviewItemInsertion(new[]{dragged.Path},identity,new Point(),1);
                bool crossed=await target.CommitItemInsertionAsync(new[]{dragged.Path},identity); await Task.Delay(100);
                Check("Cross-box animated drop inserts at the target position and retains source bytes",crossed && second.Items[1].Id==dragged.Id && !first.Items.Contains(dragged) && File.ReadAllText(dragged.Path)=="original source bytes");
                Check("Dropping into a manually sized full row retains its required extra height",second.Height>112 && target.Height>=second.Height);
                manager.MoveReference(first,second.Id,dragged.Id,0);
                foreach(var item in second.Items.ToArray())manager.RemoveReference(second,item);
                second.AutoSize=true; manager.Update(second);
                // Corner resize uses the same geometry as captured mouse movement.
                first.AutoSize=false; first.Width=412; first.Height=244; first.Left=600; first.Top=400; manager.Update(first);
                bool corners=true;
                foreach(var corner in new[]{new Vector(-1,-1),new Vector(-1,1)})
                {
                    double oldRight=first.Left+first.Width,oldBottom=first.Top+first.Height,oldLeft=first.Left,oldTop=first.Top;
                    window.PreviewCornerResize(corner,new Vector(corner.X*40,corner.Y*40));
                    corners&=Math.Abs(first.Width-476)<.1 && Math.Abs(first.Height-310)<.1
                        && Math.Abs(corner.X<0?first.Left+first.Width-oldRight:first.Left-oldLeft)<.1
                        && Math.Abs(corner.Y<0?first.Top+first.Height-oldBottom:first.Top-oldTop)<.1;
                    first.Width=412; first.Height=244; first.Left=600; first.Top=400; manager.Update(first);
                }
                Check("Left resize corners snap to full icon cells and retain their opposite anchor",corners,window.InspectLayout());
                bool edgeResize=true;
                foreach(var direction in new[]{new Vector(-1,0),new Vector(1,0),new Vector(0,1)})
                {
                    var thumb=Descendants<Thumb>(window).Single(control=>control.Tag is Vector tag && tag==direction);
                    window.UpdateLayout();
                    var hit=window.InputHitTest(window.PointFromScreen(thumb.PointToScreen(new Point(thumb.ActualWidth/2,thumb.ActualHeight/2)))) as DependencyObject;
                    edgeResize&=thumb.IsVisible && hit is not null && (hit==thumb || thumb.IsAncestorOf(hit));
                    window.PreviewCornerResize(direction,new Vector(direction.X==0?17:direction.X*40,direction.Y==0?17:40));
                    edgeResize&=first.Width==(direction.X==0?412:476) && first.Height==(direction.Y==0?244:310)
                        && first.Top==400 && (direction.X<0?first.Left+first.Width==1012:first.Left==600);
                    first.Width=412; first.Height=244; first.Left=600; first.Top=400; manager.Update(first);
                }
                Check("Expanded left right and bottom edges resize one axis on the icon grid",edgeResize,window.InspectLayout());
                window.UpdateLayout();
                var collapse=Descendants<Button>(window).Single(button=>button.ToolTip?.ToString()=="展开 / 收起");
                bool hitClear=true; var hits=new List<string>();
                foreach(var point in new[]{new Point(collapse.ActualWidth/2,collapse.ActualHeight/2),new Point(collapse.ActualWidth-6,6)})
                {
                    var hit=window.InputHitTest(window.PointFromScreen(collapse.PointToScreen(point))) as DependencyObject;
                    hits.Add(hit?.GetType().Name??"null");
                    hitClear&=hit is not null && (hit==collapse || collapse.IsAncestorOf(hit));
                }
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); bool folded=first.Collapsed;
                window.UpdateLayout();
                bool edgesHidden=Descendants<Thumb>(window).Where(control=>control.Tag is Vector tag && (tag.X==0 || tag.Y==0)).All(control=>!control.IsVisible);
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Right-side collapse button remains unobstructed and toggles both ways",hitClear && folded && !first.Collapsed && edgesHidden,new { hitClear,folded,first.Collapsed,edgesHidden,hits });
                bool activated=window.Activate(),focused=collapse.Focus(),cleanSurface=true;
                for(int click=0;click<12;click++)
                {
                    collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                    cleanSurface&=collapse.Template.FindName("surface",collapse) is Border face && face.BorderThickness==new Thickness(0)
                        && collapse.RenderTransform.Value.IsIdentity;
                }
                Capture(window,Path.Combine(directory,"collapse-no-residual-outline.png"));
                Check("Repeated collapse clicks leave no persistent focus border or rotated button frame",focused && cleanSurface && !first.Collapsed && collapse.FocusVisualStyle is not null,new { activated,focused,cleanSurface,collapse.IsKeyboardFocused,first.Collapsed });
                window.PreviewCornerResize(new Vector(-1,-1),new Vector(-30,-20),true);
                Check("Cancelling corner resize restores size and position",first.Width==412 && first.Height==244 && first.Left==600 && first.Top==400);
                first.AutoSize=true; manager.Update(first); await Task.Delay(200);
                var shellBox=manager.Configuration.Boxes[2];
                var shellWindow=manager.DesktopWindows.Single(box=>box.BoxId==shellBox.Id);
                var shellData=new DataObject(); shellData.SetData(DesktopShellReference.ShellIdList,new MemoryStream(DesktopShellReference.CreateRecycleIdList()));
                var shellPaths=DesktopShellReference.ReadPaths(shellData);
                shellWindow.PreviewItemInsertion(shellPaths,null,new Point(),0);
                bool shellDropped=await shellWindow.CommitItemInsertionAsync(shellPaths,null); await Task.Delay(200); shellWindow.UpdateLayout();
                Check("Explorer Recycle Bin PIDL drop shows a native icon in the actual mapping box",shellDropped && shellBox.Items.Count==1 && shellBox.Items[0].Path==DesktopShellReference.RecycleBin
                    && Descendants<Image>(shellWindow).Any(image=>image.Width==32 && image.Source is BitmapSource bitmap && bitmap.PixelWidth>=32),shellWindow.InspectLayout());
                Capture(shellWindow,Path.Combine(directory,"recycle-bin-box.png"));
                var addRecycle=shellWindow.BoxContextMenu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="添加回收站");
                addRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(120);
                var recycle=shellBox.Items.Single();
                var recycleDrag=DesktopShellReference.CreateDragData(recycle);
                window.PreviewItemInsertion(DesktopShellReference.ReadPaths(recycleDrag),shellBox.Id+":"+recycle.Id,new Point(),1);
                bool shellMoved=await window.CommitItemInsertionAsync(new[]{recycle.Path},shellBox.Id+":"+recycle.Id);
                Check("Recycle Bin menu deduplicates and cross-box movement preserves its Shell identity",shellMoved && shellBox.Items.Count==0 && first.Items[1].Id==recycle.Id && first.Items[1].Path==DesktopShellReference.RecycleBin);
                manager.RemoveReference(first,recycle);
                var reference=first.Items.Single(item=>item.Path==original);
                Check("Cross-box move transfers only the mapping",manager.MoveReference(second,first.Id,reference.Id) && !first.Items.Contains(reference) && second.Items.Contains(reference) && File.Exists(original));
                manager.Group!.TogglePinned(); await Task.Delay(100);
                Check("Shared group hide hides every box and releases all box material resources",!manager.Configuration.GroupPinned && manager.DesktopWindows.All(box=>!box.IsVisible && !box.HasMaterialResources),manager.Group.Inspect());
                var groupHandle=app.Windows.OfType<DesktopBoxGroupHandle>().Single();
                Check("Total organizer itself collapses to a lightweight hover strip",groupHandle.Compact && !groupHandle.HasMaterialResources && groupHandle.Width<100,manager.Group.Inspect());
                long pointerTime=Environment.TickCount64;
                manager.Group.PollPointerForProbe(new Point(-10000,-10000),pointerTime);
                await Task.Delay(60); var peek=groupHandle.ScreenBounds;
                manager.Group.PollPointerForProbe(new Point(peek.Left+peek.Width/2,peek.Top+peek.Height/2),pointerTime+100);
                bool stagedTogether=groupHandle.Opacity==0 && manager.DesktopWindows.All(box=>box.Opacity==0);
                await Until(()=>!manager.Group.Revealing);
                Check("Total organizer and every branch reveal in one shared display commit",stagedTogether && manager.Group.RevealFrame>0 && groupHandle.Opacity==1 && manager.DesktopWindows.All(box=>box.Opacity==1),manager.Group.Inspect());
                Check("Hovering the compact total organizer restores it and every box",manager.Group.Shown && !groupHandle.Compact && manager.DesktopWindows.All(box=>box.IsVisible));
                manager.Group.PollPointerForProbe(new Point(-10000,-10000),Environment.TickCount64+700); await Task.Delay(60);
                Check("Leaving total organizer automatically hides both control and boxes",!manager.Group.Shown && groupHandle.Compact && manager.DesktopWindows.All(box=>!box.IsVisible));
                manager.Group.TogglePinned(); await Task.Delay(250);
                Check("Shared group reveal restores all mapping boxes and original references",manager.Configuration.GroupPinned && manager.DesktopWindows.All(box=>box.IsVisible) && first.Items.Count==29 && second.Items.Count==1,manager.Group.Inspect());
                manager.Group.PollPointerForProbe(new Point(-10000,-10000),pointerTime+2000);
                Check("Pinned total organizer stays visible after the pointer leaves",manager.Group.Shown && !groupHandle.Compact);
                double handleLeft=groupHandle.Left,handleTop=groupHandle.Top;
                manager.Group.PreviewHandleDragForProbe(new Vector(-160,120)); await Task.Delay(80);
                Check("Mouse-grip movement supports free total-organizer placement",manager.Configuration.GroupEdge=="Free" && Math.Abs(groupHandle.Left-handleLeft)>100 && Math.Abs(groupHandle.Top-handleTop)>80,new { groupHandle.Left,groupHandle.Top });
                CheckSpacing("free group movement");
                double committedLeft=groupHandle.Left,committedTop=groupHandle.Top;
                manager.Group.PreviewHandleDragForProbe(new Vector(30,20),true); await Task.Delay(50);
                Check("Cancelling a total-organizer drag restores its placement",Math.Abs(groupHandle.Left-committedLeft)<.1 && Math.Abs(groupHandle.Top-committedTop)<.1);
                Check("A single shared group handle replaces per-box pin buttons",app.Windows.OfType<DesktopBoxGroupHandle>().Count()==1 && manager.DesktopWindows.All(box=>!Descendants<Button>(box).Any(button=>button.ToolTip?.ToString()=="固定 / 自动隐藏")));
                foreach(string edge in new[]{"Left","Right","Top"})
                {
                    manager.Group.SetEdge(edge); await Task.Delay(50);
                    Check("Whole mapping group docks at "+edge,manager.Configuration.GroupEdge==edge && manager.DesktopWindows.All(box=>box.IsVisible));
                    CheckSpacing("docking "+edge);
                }

                int heartbeat=0; var beat=new DispatcherTimer(DispatcherPriority.Input) { Interval=TimeSpan.FromMilliseconds(20) };
                beat.Tick+=(_,_)=>heartbeat++; beat.Start();
                var watch=Stopwatch.StartNew(); var originalBounds=window.VisibleBoundsPixels; double inputLatencyMax=0;
                var dock=Program.Dock.VisibleSurfaceScreenBounds;
                for(int step=0;step<50;step++)
                {
                    double amount=step/49d;
                    var desired=new Rect(originalBounds.Left+(dock.Left-originalBounds.Left)*amount,
                        originalBounds.Top+(dock.Top-originalBounds.Top)*amount,originalBounds.Width,originalBounds.Height);
                    window.PreviewMoveForProbe(desired);
                    var inputWatch=Stopwatch.StartNew(); await app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);
                    inputLatencyMax=Math.Max(inputLatencyMax,inputWatch.Elapsed.TotalMilliseconds); await Task.Delay(12);
                }
                window.FinishMoveForProbe(); beat.Stop();
                Check("Dragging mapping box beneath Dock keeps dispatcher responsive",heartbeat>0 && inputLatencyMax<250 && watch.ElapsedMilliseconds<5000,new { heartbeat,inputLatencyMax,watch.ElapsedMilliseconds,layout=window.InspectLayout() });
                var neighbor=app.Windows.OfType<DesktopBoxWindow>().Single(box=>box.BoxId==second.Id);
                var otherBounds=neighbor.VisibleBoundsPixels;
                window.PreviewMoveForProbe(new Rect(otherBounds.Right-window.VisibleBoundsPixels.Width+4,otherBounds.Bottom+12,window.VisibleBoundsPixels.Width,window.VisibleBoundsPixels.Height));
                await Task.Delay(100);
                var aligned=window.VisibleBoundsPixels;
                Check("Real desktop box aligns with neighbor edge",Math.Abs(aligned.Right-otherBounds.Right)<=1,new { aligned.Right,neighborRight=otherBounds.Right });
                Capture(window,Path.Combine(directory,"mapping-box.png")); window.FinishMoveForProbe();
                CheckSpacing("individual branch movement");

                var menu=window.BoxContextMenu;
                menu.Placement=PlacementMode.AbsolutePoint; menu.HorizontalOffset=120; menu.VerticalOffset=160; menu.IsOpen=true;
                await Until(()=>!menu.IsOpen || Descendants<Styles.GlassMenuSurface>(menu).Any(surface=>surface.HasOpticalMaterial),2000);
                if(!menu.IsOpen)
                {
                    menu.IsOpen=true;
                    await Until(()=>!menu.IsOpen || Descendants<Styles.GlassMenuSurface>(menu).Any(surface=>surface.HasOpticalMaterial),2000);
                }
                var surface=Descendants<Styles.GlassMenuSurface>(menu).FirstOrDefault();
                Check("Context menu uses real Liquid Glass renderer",menu.IsOpen && surface?.HasOpticalMaterial==true,new { menu.IsOpen,optical=surface?.HasOpticalMaterial });
                var menuSource=PresentationSource.FromVisual(menu) as HwndSource;
                Check("Liquid Glass context menu allows external screenshot and recording",menuSource is not null && GetWindowDisplayAffinity(menuSource.Handle,out uint menuAffinity) && menuAffinity==0);
                Capture(menu,Path.Combine(directory,"glass-context-menu.png"));
                Check("Menu text receives an explicit readable material foreground",menu.Items.OfType<MenuItem>().All(item=>item.Foreground is SolidColorBrush ink && (ink.Color==Colors.White || ink.Color==Color.FromRgb(35,43,55))),menu.Items.OfType<MenuItem>().Select(item=>item.Foreground.ToString()).ToArray());
                Check("Glass context menu preserves outside-dismiss observers",dismissal.OpenMenuCount==1 && dismissal.HookCount==2);
                menu.IsOpen=false; var closing=Stopwatch.StartNew();
                while((dismissal.OpenMenuCount!=0 || dismissal.HookCount!=0) && closing.ElapsedMilliseconds<1000)await Task.Delay(40);
                Check("Closing glass menu releases observers",dismissal.OpenMenuCount==0 && dismissal.HookCount==0,new { dismissal.OpenMenuCount,dismissal.HookCount,closing.ElapsedMilliseconds });

                Program.Sidebar.OpenPanel(ControlSection.Overview); await Task.Delay(30);
                var networkButton=Descendants<Button>(Program.Sidebar).Single(button=>button.ToolTip?.ToString()=="Wi-Fi 与网络");
                networkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var navigation=Stopwatch.StartNew();
                var networkPanel=app.Windows.OfType<QuickControlsWindow>().Single();
                Check("Dock, sidebar, control center, organizer and boxes all allow external capture",app.Windows.OfType<GlassWindow>().All(owned=>GetWindowDisplayAffinity(new WindowInteropHelper(owned).Handle,out uint affinity) && affinity==0));
                while(!networkPanel.SectionReady && navigation.ElapsedMilliseconds<6000)await Task.Delay(40);
                await app.Dispatcher.InvokeAsync(()=>networkPanel.UpdateLayout(),DispatcherPriority.Loaded);
                await Task.Delay(250);
                Check("Sidebar network button navigates to the live WLAN control-center page",networkPanel.IsVisible && networkPanel.CurrentSection==ControlSection.Wifi && networkPanel.SectionReady,networkPanel.InspectLayout());
                Capture(networkPanel,Path.Combine(directory,"network-page.png"));
                networkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool toggledClosed=!networkPanel.IsVisible;
                networkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var reopened=app.Windows.OfType<QuickControlsWindow>().Single();
                Check("Clicking the active sidebar button closes its panel and the next click reopens it",toggledClosed && reopened.IsVisible && reopened.CurrentSection==ControlSection.Wifi);
                var levels=new StackPanel { Orientation=Orientation.Horizontal,Background=Brushes.Black };
                foreach(double level in new[]{.3,.5,.9})
                {
                    var column=new StackPanel { Margin=new Thickness(12) };
                    var icon=new Sidebar.VolumeLevelIcon { Width=28,Height=28,Ink=Brushes.White };
                    icon.Update(new Services.AudioState(level,false,true,null));
                    column.Children.Add(icon); column.Children.Add(Ui.Text($"{level:P0}",12,Brushes.White)); levels.Children.Add(column);
                }
                levels.Measure(new Size(240,90)); levels.Arrange(new Rect(0,0,levels.DesiredSize.Width,levels.DesiredSize.Height));
                Capture(levels,Path.Combine(directory,"volume-waves.png"));
                Program.Sidebar.OpenPanel(ControlSection.Apps); await Task.Delay(200);
                var controls=app.Windows.OfType<QuickControlsWindow>().Single();
                using var fixture=new HwndSource(new HwndSourceParameters("GlassDock owned native tray menu") { WindowStyle=unchecked((int)0x80000000),Width=1,Height=1 });
                nint nativeMenu=CreatePopupMenu(); AppendMenu(nativeMenu,0,1,"Owned tray menu fixture");
                bool held=false,menuVisible=false,railVisible=false,panelVisible=false;
                nint sidebarHandle=new WindowInteropHelper(Program.Sidebar).Handle,panelHandle=new WindowInteropHelper(controls).Handle;
                trayMenus.Begin(fixture.Handle);
                using var cancel=new System.Threading.Timer(_=> {
                    held=trayMenus.Active;
                    var native=FindWindow("#32768",null); menuVisible=native!=0 && IsWindowVisible(native);
                    railVisible=IsWindowVisible(sidebarHandle); panelVisible=IsWindowVisible(panelHandle);
                    PostMessage(fixture.Handle,0x001F,0,0);
                },null,650,Timeout.Infinite);
                try { TrackPopupMenuEx(nativeMenu,0x0100|0x0080,420,300,fixture.Handle,0); }
                finally { DestroyMenu(nativeMenu); }
                await Task.Delay(350);
                Check("Native tray menu keeps sidebar and control center visible",held && menuVisible && railVisible && panelVisible,new { held,menuVisible,railVisible,panelVisible });
                Check("Native tray menu session ends after dismissal",!trayMenus.Active);
                var preservedPrefs=Program.Prefs;
                var settingsOrder=first.Items.Select(item=>item.Id).ToArray();
                Program.Prefs=new Preferences {ReplaceTaskbar=false,SidebarPinned=true,DockScale=.5,IconSize=40,Magnification=.4,GlassTransparency=.4};
                Program.ApplyPreferences();
                var settings=new SettingsWindow(); settings.Show(); await Task.Delay(100);
                Check("Live transparency changes only the glass layer across Dock, sidebar, boxes and control center",app.Windows.OfType<GlassWindow>().Where(window=>window.IsVisible).All(window=>Math.Abs(window.GlassLayerOpacity-.6)<.0001 && window.ContentLayerOpacity==1));
                var glassSlider=Descendants<UnifiedSlider>(settings).Single(slider=>System.Windows.Automation.AutomationProperties.GetName(slider)=="玻璃层透明度");
                glassSlider.Value=1; await Task.Delay(80);
                Check("Fully transparent glass releases its render resources while content remains interactive",Rendering.SharedDesktopCapture.ActiveOutputCount==0 && Program.Dock.ContentLayerOpacity==1 && Program.Sidebar.ContentLayerOpacity==1);
                Descendants<ComboBox>(settings).Single().SelectedIndex=0;
                foreach(string label in new[]{"玻璃层透明度","Dock 整体尺寸","图标大小","磁吸放大","材质刷新率"})
                    Descendants<Button>(settings).Single(button=>button.ToolTip?.ToString()=="重置"+label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(180); var defaults=new Preferences();
                Check("Each appearance slider reset restores its own canonical default immediately",Program.Prefs.GlassTransparency==defaults.GlassTransparency && Program.Prefs.DockScale==defaults.DockScale && Program.Prefs.IconSize==defaults.IconSize && Program.Prefs.Magnification==defaults.Magnification && Program.Prefs.GlassFramesPerSecond==defaults.GlassFramesPerSecond);
                Check("Appearance reset preserves mapped items and original file bytes",settingsOrder.SequenceEqual(first.Items.Select(item=>item.Id)) && File.ReadAllText(original)=="original source bytes");
                settings.Close(); Program.Prefs=preservedPrefs; Program.ApplyPreferences();
                Check("File source survives all visual and reference operations",File.ReadAllText(original)=="original source bytes");
                var syncBox=manager.Configuration.Boxes[2];
                var syncWindow=manager.DesktopWindows.Single(box=>box.BoxId==syncBox.Id);
                string parent=Path.Combine(directory,"动态资料"),nested=Path.Combine(parent,"二级"); Directory.CreateDirectory(nested);
                string child=Path.Combine(nested,"旧名称.txt"); File.WriteAllText(child,"hierarchy source bytes");
                await manager.AddReferencesAsync(syncBox,new[]{parent,nested,child,DesktopShellReference.RecycleBin});
                var childItem=syncBox.Items.Single(item=>item.Path==child); var identities=syncBox.Items.Select(item=>item.Id).ToArray();
                Check("Existing-source directory monitoring starts without importing new desktop files",await Until(()=>manager.WatchesSource(child)),manager.Inspect());
                string renamedChild=Path.Combine(nested,"新名称.txt"); File.Move(child,renamedChild);
                Check("File rename updates the mapped path and caption without changing identity",await Until(()=>syncBox.Items.Any(item=>item.Id==childItem.Id && item.Path==renamedChild && item.Name=="新名称")),manager.Inspect());
                syncWindow.UpdateLayout();
                Check("Renamed item rebuilds its visible caption and launch data",await Until(()=>Descendants<Button>(syncWindow).Any(button=>button.DataContext is DesktopBoxItem item && item.Id==childItem.Id && item.Path==renamedChild && button.ToolTip?.ToString()?.Contains(renamedChild)==true)),Descendants<Button>(syncWindow).Where(button=>button.DataContext is DesktopBoxItem).Select(button=>new { item=(DesktopBoxItem)button.DataContext,button.ToolTip }).ToArray());
                string renamedParent=Path.Combine(directory,"动态资料改名"); Directory.Move(parent,renamedParent);
                Check("Parent rename remaps nested folder and file references while retaining their order",await Until(()=>syncBox.Items.Where(item=>!DesktopShellReference.IsRecycleBin(item.Path)).All(item=>DesktopReferenceMonitor.Within(item.Path,renamedParent))) && syncBox.Items.Select(item=>item.Id).SequenceEqual(identities),manager.Inspect());
                string outside=Path.Combine(Path.GetTempPath(),"GlassDock-reference-move-"+Guid.NewGuid().ToString("N"));
                Directory.Move(renamedParent,outside); NotifyShellRename(0x20000,0x2005,renamedParent,outside);
                Check("Native Shell move notifications follow a folder outside the previous watched parent",await Until(()=>syncBox.Items.Where(item=>!DesktopShellReference.IsRecycleBin(item.Path)).All(item=>DesktopReferenceMonitor.Within(item.Path,outside))) && syncBox.Items.Count==4,manager.Inspect());
                string movedChild=Path.Combine(outside,"二级","新名称.txt");
                Check("Source watches adapt to the moved folder hierarchy",await Until(()=>manager.WatchesSource(movedChild)),manager.Inspect());
                string untracked=Path.Combine(outside,"未收纳的新文件.txt"); File.WriteAllText(untracked,"not imported");
                await Task.Delay(850);
                Check("New untracked files are not automatically added to any mapping box",!manager.Configuration.Boxes.SelectMany(box=>box.Items).Any(item=>item.Path==untracked) && syncBox.Items.Count==4);
                double beforeDeleteHeight=syncWindow.Height; File.Delete(movedChild);
                Check("Deleting a source removes its icon and compacts the adaptive box",await Until(()=>syncBox.Items.All(item=>item.Id!=childItem.Id)) && syncBox.Items.Count==3 && syncWindow.Height<beforeDeleteHeight && syncBox.Items.Any(item=>item.Path==outside),syncWindow.InspectLayout());
                string replacing=Path.Combine(outside,"替换中的文件.txt"),backup=replacing+".old"; File.WriteAllText(replacing,"before");
                await manager.AddReferencesAsync(syncBox,new[]{replacing}); string replacementId=syncBox.Items.Single(item=>item.Path==replacing).Id;
                File.Move(replacing,backup); File.WriteAllText(replacing,"after atomic replacement"); await Task.Delay(1400);
                Check("An editor-style atomic replacement preserves the original mapping",syncBox.Items.Any(item=>item.Id==replacementId && item.Path==replacing) && !syncBox.Items.Any(item=>item.Path==backup));
                var usedRoots=DriveInfo.GetDrives().Select(drive=>drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string unusedRoot=Enumerable.Range('D',23).Select(value=>((char)value)+":\\").First(root=>!usedRoots.Contains(root));
                var unavailable=new DesktopBoxItem(Guid.NewGuid().ToString("N"),"暂不可用",unusedRoot+"GlassDock-unavailable-source.txt"); syncBox.Items.Add(unavailable); manager.Update(syncBox);
                File.Delete(backup); File.Delete(replacing); File.Delete(untracked); Directory.Delete(Path.Combine(outside,"二级")); Directory.Delete(outside);
                Check("Deleting a folder removes mapped descendants while retaining Recycle Bin and unavailable sources",await Until(()=>syncBox.Items.Count==2) && syncBox.Items.Any(item=>DesktopShellReference.IsRecycleBin(item.Path)) && syncBox.Items.Contains(unavailable),manager.Inspect());
                for(int added=manager.Configuration.Boxes.Count;added<8;added++)manager.CreateBox("拖动压力 "+added);
                manager.Group!.PreviewHandleDragForProbe(new Vector(-600,160)); await Task.Delay(250);
                var allocationsBefore=manager.DesktopWindows.Sum(box=>box.MaterialAllocationGeneration);
                long framesBefore=manager.Group.DragCounters.Frames;
                long allocatedBefore=GC.GetAllocatedBytesForCurrentThread(); var dragWatch=Stopwatch.StartNew(); double maximumInputDelay=0,maximumMoveTime=0;
                for(int gesture=0;gesture<16;gesture++)
                {
                    manager.Group.BeginRapidDragForProbe();
                    for(int sample=0;sample<12;sample++)
                    {
                        var moveWatch=Stopwatch.StartNew();
                        manager.Group.MoveRapidDragForProbe(new Vector(Math.Sin(sample*.8)*24,Math.Sin(sample*.9)*145));
                        maximumMoveTime=Math.Max(maximumMoveTime,moveWatch.Elapsed.TotalMilliseconds);
                        var inputWatch=Stopwatch.StartNew(); await app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);
                        maximumInputDelay=Math.Max(maximumInputDelay,inputWatch.Elapsed.TotalMilliseconds); await Task.Delay(2);
                    }
                    manager.Group.MoveRapidDragForProbe(new Vector()); manager.Group.EndRapidDragForProbe();
                }
                var dragPerformance=new { boxes=manager.Configuration.Boxes.Count,gestures=16,requests=16*13,elapsedMs=dragWatch.Elapsed.TotalMilliseconds,maximumInputDelay,maximumMoveTime,
                    managedAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocatedBefore,surfaceReallocations=manager.DesktopWindows.Sum(box=>box.MaterialAllocationGeneration)-allocationsBefore,appliedFrames=manager.Group.DragCounters.Frames-framesBefore };
                File.WriteAllText(Path.Combine(directory,"drag-performance.json"),JsonSerializer.Serialize(dragPerformance,new JsonSerializerOptions { WriteIndented=true }));
                Check("Repeated fast group drags retain input responsiveness with eight visible boxes",maximumInputDelay<250 && dragWatch.ElapsedMilliseconds<10000,dragPerformance);
                var beforePause=manager.Configuration.Boxes.Select(box=>new { box.Id,box.Name,Items=box.Items.Select(item=>(item.Id,item.Path)).ToArray() }).ToArray();
                bool groupPinned=manager.Configuration.GroupPinned;
                Program.Dock.PreviewResourceSuspension(true); Program.Sidebar.SetDesktopSuspended(true); manager.SetDesktopSuspended(true);
                Check("Fullscreen resource mode releases desktop capture and all material surfaces",manager.DesktopWindows.All(box=>!box.IsVisible && !box.HasMaterialResources)
                    && !Program.Dock.HasMaterialResources && !Program.Sidebar.HasMaterialResources && Rendering.SharedDesktopCapture.ActiveOutputCount==0,manager.Inspect());
                string asleep=Path.Combine(directory,"后台同步项目.txt"); File.WriteAllText(asleep,"background source");
                await manager.AddReferencesAsync(syncBox,new[]{asleep});
                await Until(()=>manager.WatchesSource(asleep)); File.Delete(asleep);
                Check("Existing-file synchronization keeps working without waking fullscreen UI",await Until(()=>syncBox.Items.All(item=>item.Path!=asleep)) && manager.DesktopWindows.All(box=>!box.IsVisible && !box.HasMaterialResources),manager.Inspect());
                Program.Dock.PreviewResourceSuspension(false); Program.Sidebar.SetDesktopSuspended(false); Program.Sidebar.RevealForCapture(); manager.SetDesktopSuspended(false);
                bool resumed=await Until(()=>manager.DesktopWindows.All(box=>box.IsVisible && box.HasMaterialResources) && Program.Dock.HasMaterialResources && Program.Sidebar.HasMaterialResources);
                Check("Returning to desktop restores all glass without losing mappings or pinned state",resumed && manager.Configuration.GroupPinned==groupPinned
                    && beforePause.All(before=>manager.Configuration.Boxes.Any(box=>box.Id==before.Id && box.Name==before.Name && box.Items.Select(item=>(item.Id,item.Path)).SequenceEqual(before.Items))),manager.Inspect());
            }
            catch(Exception exception) { failure=exception.ToString(); }
            finally
            {
                watchdog.Stop(); trayMenus.Cancel(); manager?.Dispose(); Program.DesktopBoxes=null;
                foreach(Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                string full=Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                string report=JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,mode="isolated mapping data and owned production windows; no physical mouse input",artifacts=directory,results,failure },new JsonSerializerOptions { WriteIndented=true });
                File.WriteAllText(full,report); Console.WriteLine(report); app.Shutdown(failure is null?0:1);
            }
        }));
        watchdog.Start(); return app.Run();
    }
    private static void Capture(FrameworkElement visual,string path)
    {
        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(visual.ActualWidth*1.5)),Math.Max(1,(int)Math.Ceiling(visual.ActualHeight*1.5)),144,144,PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
    {
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(node);index++)
        { var child=VisualTreeHelper.GetChild(node,index); if(child is T item)yield return item; foreach(var nested in Descendants<T>(child))yield return nested; }
    }
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool AppendMenu(nint menu,uint flags,nuint id,string label);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenuEx(nint menu,uint flags,int x,int y,nint owner,nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindow(string className,string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint w,nint l);
    [DllImport("shell32.dll",EntryPoint="SHChangeNotify",CharSet=CharSet.Unicode)] private static extern void NotifyShellRename(int kind,uint flags,string oldPath,string newPath);
}
