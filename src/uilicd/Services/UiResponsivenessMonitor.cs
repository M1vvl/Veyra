using System.Windows.Threading;

namespace GlassDock.Services;

/// <summary>One pending dispatcher ping, one timer, and logs only on a stall/recovery.
/// Does not restart the app, trim memory, change visuals, or collect file/window names.</summary>
internal sealed class UiResponsivenessMonitor : IDisposable
{
    private static string _activity="dispatcher";
    internal static ActivityScope TrackActivity(string activity)=>new(activity);
    internal readonly struct ActivityScope : IDisposable
    {
        private readonly string _previous;
        internal ActivityScope(string activity) {_previous=Volatile.Read(ref _activity); Volatile.Write(ref _activity,activity);}
        public void Dispose()=>Volatile.Write(ref _activity,_previous);
    }
    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private readonly Action<string> _record;
    private readonly int _threshold;
    private long _postedAt;
    private int _pending,_reported,_checking,_disposed;
    internal UiResponsivenessMonitor(Dispatcher dispatcher,Action<string>? record=null,int thresholdMilliseconds=2000,int periodMilliseconds=500)
    {
        _dispatcher=dispatcher; _record=record??Program.Log; _threshold=thresholdMilliseconds;
        _timer=new Timer(_=>Check(),null,periodMilliseconds,periodMilliseconds);
    }
    private void Check()
    {
        if(Interlocked.CompareExchange(ref _checking,1,0)!=0)return;
        try
        {
            if(Volatile.Read(ref _disposed)!=0 || _dispatcher.HasShutdownStarted)return;
            long now=Environment.TickCount64;
            if(Volatile.Read(ref _pending)!=0)
            {
                if(now-Volatile.Read(ref _postedAt)>=_threshold && Interlocked.CompareExchange(ref _reported,1,0)==0)
                    _record("UI stall detected; dispatcher delay >= "+(now-_postedAt)+" ms; activity="+Volatile.Read(ref _activity)+".");
                return;
            }
            Volatile.Write(ref _postedAt,now); Volatile.Write(ref _pending,1);
            _dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
            {
                long elapsed=Environment.TickCount64-Volatile.Read(ref _postedAt);
                if(Interlocked.Exchange(ref _reported,0)!=0 && Volatile.Read(ref _disposed)==0)
                    _record("UI responsiveness restored; dispatcher delay "+elapsed+" ms.");
                Volatile.Write(ref _pending,0);
            }));
        }
        catch(InvalidOperationException) { /* Dispatcher shutdown. */ }
        finally { Volatile.Write(ref _checking,0); }
    }
    public void Dispose() { if(Interlocked.Exchange(ref _disposed,1)==0)_timer.Dispose(); }
}
