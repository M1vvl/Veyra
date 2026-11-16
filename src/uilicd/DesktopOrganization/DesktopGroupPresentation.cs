using System.Diagnostics;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

/// <summary>Prepare every surface invisibly, then reveal the group in one display callback.</summary>
internal sealed class DesktopGroupPresentation : IDisposable
{
    private GlassWindow[] _windows=[];
    private Func<bool>? _valid;
    private Action? _committed;
    private readonly Stopwatch _clock=new();
    internal bool Revealing {get;private set;}
    internal long CommitFrame {get;private set;}
    internal int CommitCount {get;private set;}
    internal void Begin(IEnumerable<GlassWindow> windows,Func<bool> valid,Action committed)
    {
        if(Revealing)return;
        _windows=windows.ToArray(); _valid=valid; _committed=committed;
        foreach(var window in _windows) {window.Opacity=0; window.IsHitTestVisible=false;}
        Revealing=true; _clock.Restart(); CompositionTarget.Rendering+=Commit;
    }
    private void Commit(object? sender,EventArgs args)
    {
        if(_valid?.Invoke()!=true) {Cancel(); return;}
        bool ready=_windows.All(window=>window.IsVisible && (Program.Prefs.GlassTransparency>=1 || window.HasMaterialImage));
        if(!ready && _clock.ElapsedMilliseconds<350)return;
        // All opacity updates belong to this one WPF/DWM display turn.
        foreach(var window in _windows) {window.Opacity=1; window.IsHitTestVisible=true;}
        CommitFrame=(args as RenderingEventArgs)?.RenderingTime.Ticks??0; CommitCount++;
        var callback=_committed; Stop(); callback?.Invoke();
    }
    private void Stop() {if(Revealing)CompositionTarget.Rendering-=Commit; Revealing=false; _windows=[]; _valid=null; _committed=null; _clock.Stop();}
    internal void Cancel() {foreach(var window in _windows) {window.Opacity=1; window.IsHitTestVisible=true;} Stop();}
    public void Dispose()=>Cancel();
}
