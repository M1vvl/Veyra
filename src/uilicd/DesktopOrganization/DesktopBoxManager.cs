using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxManager : IDisposable
{
    private readonly DesktopBoxStore _store;
    private readonly Dispatcher? _dispatcher;
    private readonly Dictionary<string,DesktopBoxWindow> _windows=[];
    private DesktopBoxesSettingsWindow? _settings;
    private readonly Alignment.AlignmentGuides _guides=new();
    private DesktopBoxGroupController? _group;
    private DesktopReferenceMonitor? _referenceMonitor;
    private DesktopBoxWindow? _itemDragTarget;
    private bool _desktopSuspended;
    internal IEnumerable<DesktopBoxWindow> DesktopWindows=>_windows.Values;
    internal DesktopBoxGroupController? Group=>_group;
    internal bool WatchesSource(string path)=>_referenceMonitor?.Watches(path)==true;
    internal bool DesktopSuspended=>_desktopSuspended;
    internal Window? GroupControl=>_group?.ControlWindow;
    internal bool InteractionBusy=>_group?.Dragging==true || _windows.Values.Any(window=>window.KeepsGroupVisible);
    internal void SetDesktopSuspended(bool suspended)
    {
        if(_desktopSuspended==suspended)return;
        _desktopSuspended=suspended;
        if(!suspended)_group?.PrepareReveal();
        foreach(var window in _windows.Values)window.SetDesktopSuspended(suspended);
        _group?.SetDesktopSuspended(suspended);
    }
    private bool _started,_disposed;
    private readonly bool _initialPresetEligible;
    private readonly string? _presetSeedId;
    private Task _pendingSave=Task.CompletedTask;
    private Task _initialization=Task.CompletedTask;
    private Task<int>? _desktopImport;
    private string? _operationError;
    internal DesktopBoxConfiguration Configuration { get; }
    internal string? LastError=>_store.LastError??_operationError;
    internal int VisibleBoxCount=>_windows.Values.Count(window=>window.IsVisible);
    internal Task Initialization=>_initialization;
    internal event EventHandler? Changed;

    internal DesktopBoxManager(string? configurationPath=null)
    {
        _dispatcher=Application.Current?.Dispatcher;
        _store=new DesktopBoxStore(configurationPath);
        Configuration=_store.Load();
        _initialPresetEligible=_store.LastError is null && Configuration.PresetVersion<DesktopBoxPresets.CurrentVersion
            && (!File.Exists(_store.FilePath) || IsLegacyEmptyDefault(Configuration));
        _presetSeedId=_initialPresetEligible?Configuration.Boxes.FirstOrDefault()?.Id:null;
    }

    internal void Start()
    {
        if(_started || _disposed) return;
        _started=true; RefreshWindows();
        _group=new DesktopBoxGroupController(this);
        if(_dispatcher is not null && _store.LastError is null)
        {
            _referenceMonitor=new DesktopReferenceMonitor(_dispatcher,
                ()=>Configuration.Boxes.SelectMany(box=>box.Items).Select(item=>item.Path).ToArray(),ApplyReferenceChanges,
                ()=>_group?.Dragging==true || _windows.Values.Any(window=>window.KeepsGroupVisible));
            Changed+=RefreshSourceMonitor;
        }
        if(_store.LastError is null && !File.Exists(_store.FilePath)) Save();
        if(CanApplyInitialPreset)
            _initialization=InitializePresetAsync();
        else if(_store.LastError is null && Configuration.PresetVersion<DesktopBoxPresets.CurrentVersion)
        { Configuration.PresetVersion=DesktopBoxPresets.CurrentVersion; Save(); }
    }

    private static bool IsLegacyEmptyDefault(DesktopBoxConfiguration configuration)=>configuration.Enabled
        && configuration.Boxes.Count==1 && configuration.Boxes[0] is { Name:"桌面收纳",Items.Count:0,Enabled:true,Collapsed:false,Pinned:false,Width:320,Height:280 };

    private bool CanApplyInitialPreset=>_initialPresetEligible && Configuration.Enabled
        && Configuration.PresetVersion<DesktopBoxPresets.CurrentVersion && Configuration.Boxes.Count==1
        && Configuration.Boxes[0].Id==_presetSeedId && Configuration.Boxes[0].Items.Count==0;

    private async Task InitializePresetAsync()
    {
        try
        {
            var scan=await DesktopBoxPresets.ScanDesktopAsync().ConfigureAwait(false);
            await OnOwnerAsync(()=>ApplyInitialPreset(scan)).ConfigureAwait(false);
        }
        catch(Exception exception)
        {
            await OnOwnerAsync(()=> {
                if(_disposed) return;
                _operationError=exception.Message;
                Changed?.Invoke(this,EventArgs.Empty);
                Program.Log("Desktop preset: "+exception.Message);
            }).ConfigureAwait(false);
        }
    }

    private void ApplyInitialPreset(DesktopScanResult scan)
    {
        if(_disposed || !CanApplyInitialPreset) return;
        var boxes=DesktopBoxPresets.Create(scan.Items);
        DesktopBoxPresets.Arrange(boxes,AvailableDesktopArea());
        var previousBoxes=Configuration.Boxes;
        bool previousEnabled=Configuration.Enabled;
        Configuration.Boxes=boxes;
        Configuration.Enabled=true;
        try { RefreshWindows(); }
        catch
        {
            Configuration.Boxes=previousBoxes;
            Configuration.Enabled=previousEnabled;
            try { RefreshWindows(); } catch(Exception restoreError) { Program.Log("Desktop preset restore: "+restoreError.Message); }
            throw;
        }
        _operationError=scan.Error;
        Configuration.PresetVersion=DesktopBoxPresets.CurrentVersion;
        Save(); Changed?.Invoke(this,EventArgs.Empty);
    }

    private Task OnOwnerAsync(Action action)
    {
        if(_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        if(_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return Task.CompletedTask;
        return _dispatcher.InvokeAsync(action,DispatcherPriority.Normal).Task;
    }

    internal void ShowSettings()
    {
        if(_disposed) return;
        if(_settings is not null) { _settings.Activate(); return; }
        _settings=new DesktopBoxesSettingsWindow(this);
        _group?.Hold(_settings);
        _settings.Closed+=(_,_)=>_settings=null;
        _settings.Show(); _settings.Activate();
    }

    internal void PromptCreateBox()
    {
        var editor=new BoxNameWindow("新建映射盒","映射盒 "+(Configuration.Boxes.Count+1),name=>CreateBox(name));
        _group?.Hold(editor); editor.Show();
    }
    internal void PromptRename(DesktopBoxDefinition box)
    {
        var editor=new BoxNameWindow("重命名",box.Name,name=> { box.Name=name; Update(box); });
        _group?.Hold(editor); editor.Show();
    }
    internal void RevealGroup()=>_group?.Touch();

    internal DesktopBoxDefinition CreateBox(string? name=null)
    {
        if(Configuration.Boxes.Count>=DesktopBoxConfiguration.MaxBoxes) throw new InvalidOperationException("最多 64 个映射盒");
        ClaimConfiguration();
        var box=new DesktopBoxDefinition { Name=string.IsNullOrWhiteSpace(name)?"映射盒 "+(Configuration.Boxes.Count+1):name.Trim()[..Math.Min(40,name.Trim().Length)] };
        var size=DesktopBoxLayout.AdaptiveSize(0,AvailableDesktopArea().Size); box.Width=size.Width; box.Height=size.Height;
        DesktopBoxPresets.Arrange(new[]{box},AvailableDesktopArea(),OccupiedAreas());
        Configuration.Boxes.Add(box); Configuration.Enabled=true; RefreshWindows(); Save(); Changed?.Invoke(this,EventArgs.Empty); return box;
    }

    internal void RemoveBox(DesktopBoxDefinition box)
    {
        ClaimConfiguration();
        Configuration.Boxes.Remove(box); RefreshWindows(); Save(); Changed?.Invoke(this,EventArgs.Empty);
    }

    internal void Update(DesktopBoxDefinition? box=null,bool refresh=false)
    {
        ClaimConfiguration();
        if(refresh) RefreshWindows();
        else if(box is not null && _windows.TryGetValue(box.Id,out var window)) window.RefreshModel();
        Save(); Changed?.Invoke(this,EventArgs.Empty);
    }

    internal async Task AddReferencesAsync(DesktopBoxDefinition box,IEnumerable<string> paths,int? insertionIndex=null)
    {
        string[] candidates=paths.Take(500).ToArray();
        await OnOwnerAsync(ClaimConfiguration).ConfigureAwait(false);
        var references=await Task.Run(()=> {
            var items=new List<DesktopBoxItem>();
            foreach(string candidate in candidates)
            {
                if(DesktopShellReference.IsRecycleBin(candidate)) { items.Add(DesktopShellReference.CreateRecycleBin()); continue; }
                string path;
                try { path=Path.GetFullPath(candidate); }
                catch(ArgumentException) { continue; }
                if(!File.Exists(path) && !Directory.Exists(path)) continue;
                bool isDirectory=Directory.Exists(path);
                string name=isDirectory?new DirectoryInfo(path).Name:Path.GetFileNameWithoutExtension(path);
                items.Add(new(Guid.NewGuid().ToString("N"),name,path,isDirectory));
            }
            return items;
        }).ConfigureAwait(false);
        await OnOwnerAsync(()=> {
            if(_disposed || !Configuration.Boxes.Contains(box)) return;
            int insert=Math.Clamp(insertionIndex??box.Items.Count,0,box.Items.Count);
            foreach(var item in references)
            {
                if(box.Items.Count>=500) break;
                if(box.Items.Any(existing=>string.Equals(existing.Path,item.Path,StringComparison.OrdinalIgnoreCase))) continue;
                box.Items.Insert(insert++,item);
            }
            Update(box);
        }).ConfigureAwait(false);
    }

    internal Task<int> AddDesktopItemsAsync()
    {
        if(_disposed) return Task.FromResult(0);
        if(_desktopImport is { IsCompleted:false }) return _desktopImport;
        return _desktopImport=AddDesktopItemsCoreAsync();
    }

    private async Task<int> AddDesktopItemsCoreAsync()
    {
        await OnOwnerAsync(()=> { ClaimConfiguration(); _operationError=null; }).ConfigureAwait(false);
        var scan=await DesktopBoxPresets.ScanDesktopAsync().ConfigureAwait(false);
        int added=0;
        await OnOwnerAsync(()=>added=ApplyDesktopItems(scan)).ConfigureAwait(false);
        return added;
    }

    private int ApplyDesktopItems(DesktopScanResult scan)
    {
        if(_disposed) return 0;
        _operationError=scan.Error;
        var referenced=new HashSet<string>(Configuration.Boxes.SelectMany(box=>box.Items).Select(item=>item.Path),StringComparer.OrdinalIgnoreCase);
        var newBoxes=new List<DesktopBoxDefinition>();
        int added=0;
        foreach(string group in new[]{DesktopBoxPresets.Applications,DesktopBoxPresets.Files})
        {
            var pending=scan.Items.Where(item=>DesktopBoxPresets.GroupOf(item)==group && !referenced.Contains(item.Path)).ToArray();
            if(pending.Length==0) continue;
            var target=Configuration.Boxes.FirstOrDefault(box=>box.DesktopGroup==group && box.Items.Count<500);
            if(target is null)
            {
                if(Configuration.Boxes.Count>=DesktopBoxConfiguration.MaxBoxes) { _operationError="最多 64 个映射盒"; continue; }
                // A single empty starter is reusable; explicitly named user boxes stay intact.
                target=Configuration.Boxes.Count==1 && Configuration.Boxes[0] is { Name:"桌面收纳",Items.Count:0,DesktopGroup:null }
                    ?Configuration.Boxes[0]:null;
                if(target is null)
                {
                    target=new() { Name=group==DesktopBoxPresets.Applications?"桌面应用":"桌面文件",DesktopGroup=group };
                    Configuration.Boxes.Add(target); newBoxes.Add(target);
                }
                else target.DesktopGroup=group;
            }
            foreach(var item in pending)
            {
                if(target.Items.Count>=500) break;
                if(!referenced.Add(item.Path)) continue;
                target.Items.Add(item); added++;
            }
            target.Enabled=true;
        }
        DesktopBoxPresets.Arrange(newBoxes,AvailableDesktopArea(),OccupiedAreas(newBoxes));
        if(added>0) Configuration.Enabled=true;
        RefreshWindows(); Save(); Changed?.Invoke(this,EventArgs.Empty);
        return added;
    }

    private void ClaimConfiguration()=>Configuration.PresetVersion=Math.Max(Configuration.PresetVersion,DesktopBoxPresets.CurrentVersion);

    private IEnumerable<Rect> OccupiedAreas(IReadOnlyList<DesktopBoxDefinition>? excluding=null)=>
        Configuration.Boxes.Where(box=>box.Enabled && excluding?.Contains(box)!=true).Select(box=>new Rect(box.Left,box.Top,box.Width,box.Height));

    private static Rect AvailableDesktopArea()
    {
        Rect monitor=SystemParameters.WorkArea;
        if(Program.Dock is { IsLoaded:true } dock)
        {
            var native=DesktopNative.MonitorRect(dock); var dpi=VisualTreeHelper.GetDpi(dock);
            monitor=new Rect(native.Left/dpi.DpiScaleX,native.Top/dpi.DpiScaleY,
                (native.Right-native.Left)/dpi.DpiScaleX,(native.Bottom-native.Top)/dpi.DpiScaleY);
        }
        double left=monitor.Left+(monitor.Width>900?120:16),top=monitor.Top+20;
        double right=monitor.Right-96,bottom=monitor.Bottom-120;
        if(Program.Sidebar is { IsLoaded:true } sidebar && sidebar.Left>monitor.Left && sidebar.Left<monitor.Right)
            right=Math.Min(right,sidebar.Left-16);
        if(Program.Dock is { IsLoaded:true } currentDock && currentDock.Top>monitor.Top && currentDock.Top<monitor.Bottom)
            bottom=Math.Min(bottom,currentDock.Top-12);
        return new(left,top,Math.Max(220,right-left),Math.Max(130,bottom-top));
    }

    internal void RemoveReference(DesktopBoxDefinition box,DesktopBoxItem item)
    {
        box.Items.RemoveAll(existing=>existing.Id==item.Id); Update(box);
    }
    internal bool MoveReference(DesktopBoxDefinition target,string sourceId,string itemId,int? insertionIndex=null)
    {
        var source=Configuration.Boxes.FirstOrDefault(box=>box.Id==sourceId);
        var item=source?.Items.FirstOrDefault(reference=>reference.Id==itemId);
        if(source is null || item is null || !Configuration.Boxes.Contains(target) || source!=target && target.Items.Count>=500) return false;
        source.Items.Remove(item);
        if(!target.Items.Any(reference=>string.Equals(reference.Path,item.Path,StringComparison.OrdinalIgnoreCase)))
            target.Items.Insert(Math.Clamp(insertionIndex??target.Items.Count,0,target.Items.Count),item);
        if(source!=target)Update(source);
        Update(target); return true;
    }
    internal void ClaimItemDragTarget(DesktopBoxWindow target)
    {
        if(_itemDragTarget==target)return;
        var previous=_itemDragTarget; _itemDragTarget=target; previous?.ClearItemDragPreview();
    }
    internal void ReleaseItemDragTarget(DesktopBoxWindow target) { if(_itemDragTarget==target)_itemDragTarget=null; }
    internal void ClearItemDragPreviews() { _itemDragTarget=null; foreach(var window in _windows.Values)window.ClearItemDragPreview(); }

    internal Rect PreviewDrag(DesktopBoxWindow window,Rect desired)
    {
        var dpi=VisualTreeHelper.GetDpi(window);
        var others=_windows.Values.Where(other=>other!=window && other.IsVisible).Select(other=>other.VisibleBoundsPixels).ToArray();
        var gap=new Size(DesktopBoxSpacing.Gap*dpi.DpiScaleX,DesktopBoxSpacing.Gap*dpi.DpiScaleY);
        var snap=Alignment.SnapGeometry.Evaluate(desired,others,10*dpi.DpiScaleX,gap.Width);
        var monitor=DesktopNative.MonitorRect(window);
        double x=Math.Clamp(snap.SnappedVisibleLeft??desired.Left,monitor.Left+6,Math.Max(monitor.Left+6,monitor.Right-desired.Width-6));
        double y=Math.Clamp(snap.SnappedVisibleTop??desired.Top,monitor.Top+6,Math.Max(monitor.Top+6,monitor.Bottom-desired.Height-6));
        if(Program.Dock is { IsLoaded:true } dock)
        {
            var dockBounds=dock.VisibleSurfaceScreenBounds;
            if(x<dockBounds.Right && x+desired.Width>dockBounds.Left) y=Math.Min(y,Math.Max(monitor.Top+6,dockBounds.Top-38*dpi.DpiScaleY));
        }
        var candidate=new Rect(x,y,desired.Width,desired.Height);
        var obstacles=_group is null?others:others.Append(_group.ControlSpacingBounds).ToArray();
        var area=_group?.ChildPlacementArea??new Rect(monitor.Left+6,monitor.Top+6,monitor.Right-monitor.Left-12,monitor.Bottom-monitor.Top-12);
        if(!DesktopBoxSpacing.TryPlace(candidate,obstacles,area,gap,out var placed))placed=window.VisibleBoundsPixels;
        _guides.Show(Alignment.SnapGeometry.Evaluate(placed,others,.5*dpi.DpiScaleX,gap.Width));
        return placed;
    }
    internal void EndDrag()=>_guides.Hide();

    internal void ApplyPreferences()
    {
        foreach(var window in _windows.Values) window.ApplyPreferences();
        if(_group?.ControlWindow is GlassWindow handle)handle.ApplyPreferences();
        _settings?.ApplyPreferences();
    }

    internal object Inspect()=>new
    {
        enabled=Configuration.Enabled,
        boxes=Configuration.Boxes.Count,
        visible=VisibleBoxCount,
        references=Configuration.Boxes.Sum(box=>box.Items.Count),
        presetVersion=Configuration.PresetVersion,
        initializing=!_initialization.IsCompleted,
        error=LastError,
        group=_group?.Inspect(),
        sourceSync=_referenceMonitor?.Inspect(),
        desktopSuspended=_desktopSuspended,
        windows=_windows.Values.Select(window=>window.InspectLayout()).ToArray()
    };

    private void RefreshWindows()
    {
        foreach(var pair in _windows.ToArray())
        {
            if(Configuration.Enabled && Configuration.Boxes.Any(box=>box.Id==pair.Key && box.Enabled)) continue;
            pair.Value.Close(); _windows.Remove(pair.Key);
        }
        if(!Configuration.Enabled || !_started) return;
        foreach(var box in Configuration.Boxes.Where(box=>box.Enabled))
        {
            if(_windows.TryGetValue(box.Id,out var existing)) { existing.RefreshModel(); continue; }
            var window=new DesktopBoxWindow(this,box); _windows.Add(box.Id,window);
            if(_desktopSuspended)window.SetDesktopSuspended(true); else window.Show();
        }
    }

    private void Save()=>_pendingSave=_store.SaveAsync(Configuration);
    internal void PersistLayout()=>Save();
    internal void CommitLayout() { ClaimConfiguration(); Save(); }
    private void RefreshSourceMonitor(object? sender,EventArgs args)=>_referenceMonitor?.Refresh();
    private void ApplyReferenceChanges(IReadOnlyList<ReferenceChange> changes)
    {
        if(_disposed)return;
        var byPath=changes.ToDictionary(change=>change.OldPath,StringComparer.OrdinalIgnoreCase); bool changed=false;
        foreach(var box in Configuration.Boxes)
        {
            bool dirty=false;
            for(int index=box.Items.Count-1;index>=0;index--)
            {
                var item=box.Items[index];
                if(!byPath.TryGetValue(item.Path,out var change))continue;
                if(change.NewPath is null)box.Items.RemoveAt(index);
                else
                {
                    string name=item.IsDirectory?Path.GetFileName(change.NewPath.TrimEnd(Path.DirectorySeparatorChar)):Path.GetFileNameWithoutExtension(change.NewPath);
                    box.Items[index]=item with { Path=change.NewPath,Name=name };
                }
                dirty=changed=true;
            }
            if(!dirty)continue;
            box.Items=box.Items.DistinctBy(item=>item.Path,StringComparer.OrdinalIgnoreCase).ToList();
            if(_windows.TryGetValue(box.Id,out var window))window.RefreshSourceReferences();
        }
        if(changed) { Save(); Changed?.Invoke(this,EventArgs.Empty); }
    }

    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        Changed-=RefreshSourceMonitor; _referenceMonitor?.Dispose(); _referenceMonitor=null;
        _settings?.Close();
        _group?.Dispose();
        _guides.Dispose();
        foreach(var window in _windows.Values.ToArray()) window.Close();
        _windows.Clear();
        try { _pendingSave.Wait(TimeSpan.FromSeconds(2)); } catch(AggregateException exception) { Program.Log("Desktop boxes: "+exception.GetBaseException().Message); }
    }
}
