using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlassDock.DesktopOrganization;

internal sealed record ReferenceChange(string OldPath,string? NewPath);

/// <summary>Updates existing mappings only. No file writes, new-item import, or directory enumeration.</summary>
internal sealed class DesktopReferenceMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<string[]> _snapshot;
    private readonly Action<IReadOnlyList<ReferenceChange>> _apply;
    private readonly Func<bool> _busy;
    private readonly DispatcherTimer _debounce,_fallback;
    private readonly ShellReferenceChanges _shell;
    private readonly Dictionary<string,FileSystemWatcher> _watchers=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,long> _missingSince=new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReferenceChange> _renames=[];
    private readonly ConcurrentQueue<ReferenceChange> _events=new();
    private volatile string[] _paths=[];
    private volatile bool _disposed;
    private bool _running,_again;
    private int _queued,_eventCount,_resetWatches;
    private long _passes,_removed,_renamed;
    private string? _lastError;
    internal DesktopReferenceMonitor(Dispatcher dispatcher,Func<string[]> snapshot,Action<IReadOnlyList<ReferenceChange>> apply,Func<bool> busy)
    {
        _dispatcher=dispatcher; _snapshot=snapshot; _apply=apply; _busy=busy;
        _debounce=new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval=TimeSpan.FromMilliseconds(300) };
        _debounce.Tick+=async(_,_)=> { _debounce.Stop(); await CheckAsync(); };
        _fallback=new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval=TimeSpan.FromSeconds(30) };
        _fallback.Tick+=(_,_)=>Refresh();
        _shell=new ShellReferenceChanges(Notify); _fallback.Start(); Refresh();
    }
    internal void Refresh()
    {
        if(_disposed)return;
        _paths=_snapshot().Where(path=>!DesktopShellReference.IsRecycleBin(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Schedule();
    }
    private void Notify(string path,string? destination=null)
    {
        if(_disposed || !_paths.Any(source=>Within(source,path)))return;
        if(destination is not null && Interlocked.Increment(ref _eventCount)<=2048)_events.Enqueue(new(path,destination));
        Schedule();
    }
    private void Schedule()
    {
        if(_disposed || Interlocked.Exchange(ref _queued,1)!=0 || _dispatcher.HasShutdownStarted)return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=> {
            Interlocked.Exchange(ref _queued,0); if(_disposed)return;
            _again=true; _debounce.Stop(); _debounce.Start();
        }));
    }
    private async Task CheckAsync()
    {
        if(_disposed)return;
        if(_running || _busy()) { _again=true; _debounce.Start(); return; }
        _running=true; _again=false;
        try
        {
            while(_events.TryDequeue(out var change))_renames.Add(change);
            Interlocked.Exchange(ref _eventCount,0);
            var paths=_paths; var renames=_renames.ToArray();
            var result=await Task.Run(()=> {
                var states=paths.Select(path=> {
                    var state=ReadState(path); string candidate=Resolve(path,renames);
                    bool caseRename=string.Equals(path,candidate,StringComparison.OrdinalIgnoreCase) && path!=candidate;
                    if((state==State.Missing || caseRename) && candidate!=path && !InRecycleBin(candidate))
                    {
                        var destination=ReadState(candidate);
                        if(destination==State.Present)return (Path:path,State:State.Missing,Target:(string?)candidate);
                        if(destination==State.Unavailable)return (Path:path,State:State.Unavailable,Target:(string?)candidate);
                    }
                    return (Path:path,State:state,Target:(string?)null);
                }).ToArray();
                return (states,roots:WatchRoots(paths));
            });
            if(_disposed)return;
            if(_busy()) { _again=true; _debounce.Start(); return; }
            _passes++;
            _lastError=null;
            RebuildWatches(result.roots);
            var changes=new List<ReferenceChange>(); long now=Environment.TickCount64;
            foreach(var check in result.states)
            {
                if(check.State!=State.Missing) { _missingSince.Remove(check.Path); continue; }
                if(!_missingSince.TryGetValue(check.Path,out long since)) { _missingSince[check.Path]=now; continue; }
                // Require a second, separated observation: editors and cloud clients
                // can temporarily replace a file without deleting the logical item.
                if(now-since<650)continue;
                changes.Add(new(check.Path,check.Target)); _missingSince.Remove(check.Path);
            }
            foreach(string stale in _missingSince.Keys.Where(path=>!paths.Contains(path,StringComparer.OrdinalIgnoreCase)).ToArray())_missingSince.Remove(stale);
            if(changes.Count>0)
            {
                _removed+=changes.Count(change=>change.NewPath is null); _renamed+=changes.Count(change=>change.NewPath is not null);
                _apply(changes); Refresh();
            }
            if(_missingSince.Count==0 && !result.states.Any(state=>state.State==State.Unavailable && state.Target is not null))_renames.Clear();
            if(_missingSince.Count>0 || _again)_debounce.Start();
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { _lastError=exception.Message; }
        finally { _running=false; }
    }
    internal static bool Within(string path,string ancestor)=>string.Equals(path,ancestor,StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ancestor.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    internal static string Resolve(string path,IReadOnlyList<ReferenceChange> renames)
    {
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        for(int pass=0;pass<16;pass++)
        {
            string before=path;
            foreach(var change in renames)
                if(change.NewPath is not null && Within(path,change.OldPath))
                    path=change.NewPath.TrimEnd(Path.DirectorySeparatorChar)+path[change.OldPath.TrimEnd(Path.DirectorySeparatorChar).Length..];
            if(path==before || !visited.Add(path))break;
        }
        return path;
    }
    internal static bool InRecycleBin(string path)
    {
        string root=Path.GetPathRoot(path)??"";
        return root.Length>0 && path[root.Length..].StartsWith("$Recycle.Bin"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    private enum State { Present,Missing,Unavailable }
    private static State ReadState(string path)
    {
        if(DesktopShellReference.IsRecycleBin(path))return State.Present;
        string root=Path.GetPathRoot(path)??"";
        if(root.Length==0 || GetDriveType(root) is not (2 or 3 or 6))return State.Unavailable;
        uint attributes=GetFileAttributes(path); int error=Marshal.GetLastWin32Error();
        if(attributes!=uint.MaxValue)return InRecycleBin(path)?State.Missing:State.Present;
        if(error is not (2 or 3) || GetFileAttributes(root)==uint.MaxValue)return State.Unavailable;
        return State.Missing;
    }
    private static string[] WatchRoots(string[] paths)
    {
        var roots=new List<string>();
        foreach(string path in paths)
        {
            string volume=Path.GetPathRoot(path)??"";
            if(volume.Length==0 || GetDriveType(volume) is not (2 or 3 or 6))continue;
            string? parent=Path.GetDirectoryName(path);
            while(parent is not null && GetFileAttributes(parent)==uint.MaxValue)parent=Path.GetDirectoryName(parent);
            if(parent is not null)roots.Add(parent);
        }
        var selected=new List<string>();
        foreach(string root in roots.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path=>path.Length))
        {
            if(selected.Count>=64)break;
            if(!selected.Any(existing=>Within(root,existing) && !IsVolumeRoot(existing)))selected.Add(root);
        }
        return selected.ToArray();
    }
    private static bool IsVolumeRoot(string path)=>string.Equals(path.TrimEnd('\\'),Path.GetPathRoot(path)?.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
    private void RebuildWatches(string[] roots)
    {
        bool reset=Interlocked.Exchange(ref _resetWatches,0)!=0;
        foreach(var pair in _watchers.ToArray())
            if(reset || !roots.Contains(pair.Key,StringComparer.OrdinalIgnoreCase)) { pair.Value.Dispose(); _watchers.Remove(pair.Key); }
        foreach(string root in roots)
        {
            if(_watchers.ContainsKey(root))continue;
            try
            {
                var watcher=new FileSystemWatcher(root) { IncludeSubdirectories=!IsVolumeRoot(root),NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName,InternalBufferSize=4096 };
                watcher.Deleted+=(_,e)=>Notify(e.FullPath); watcher.Created+=(_,e)=>Notify(e.FullPath);
                watcher.Renamed+=(_,e)=>Notify(e.OldFullPath,e.FullPath);
                watcher.Error+=(_,_)=> { Interlocked.Exchange(ref _resetWatches,1); Schedule(); };
                watcher.EnableRaisingEvents=true; _watchers.Add(root,watcher);
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException) { _lastError=exception.Message; }
        }
    }
    internal object Inspect()=>new { tracked=_paths.Length,watchers=_watchers.Count,shell=_shell.Registered,checking=_running,pending=_missingSince.Count,passes=_passes,removed=_removed,renamed=_renamed,error=_lastError,importsNewFiles=false };
    internal bool Watches(string path)=>_watchers.Any(pair=>Within(path,pair.Key) && (pair.Value.IncludeSubdirectories || string.Equals(Path.GetDirectoryName(path),pair.Key,StringComparison.OrdinalIgnoreCase)));
    public void Dispose()
    {
        if(_disposed)return; _disposed=true; _debounce.Stop(); _fallback.Stop(); _shell.Dispose();
        foreach(var watcher in _watchers.Values)watcher.Dispose(); _watchers.Clear();
        while(_events.TryDequeue(out _)) { }
        _missingSince.Clear(); _renames.Clear(); _paths=[];
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern uint GetDriveType(string root);
}
