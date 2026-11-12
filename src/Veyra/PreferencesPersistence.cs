using System.Windows.Threading;

namespace GlassDock;

/// <summary>Live controls apply immediately; consecutive values share one atomic disk write.</summary>
internal static class PreferencesPersistence
{
    private static DispatcherTimer? _timer;
    private static Preferences? _pending;
    internal static bool WritesSuspendedForProbe;
    internal static void Schedule(Preferences preferences)
    {
        if(WritesSuspendedForProbe)return;
        _pending=preferences;
        if(_timer is null) {_timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)}; _timer.Tick+=(_,_)=>Flush();}
        _timer.Stop(); _timer.Start();
    }
    internal static void Flush() {_timer?.Stop(); if(_pending is not { } preferences)return; _pending=null; preferences.Save();}
}
