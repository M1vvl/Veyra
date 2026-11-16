namespace GlassDock.Music;

/// <summary>SMTC song metadata and timeline notifications can arrive independently.</summary>
internal sealed class MediaTimelineGuard
{
    private bool _pending;
    private double _previousPosition,_previousDuration;
    private DateTimeOffset _previousUpdate,_changedAt;

    internal void Begin(double position,double duration,DateTimeOffset update,DateTimeOffset now)
    {
        _pending=true;
        _previousPosition=position;
        _previousDuration=duration;
        _previousUpdate=update;
        _changedAt=now;
    }

    internal void Clear()=>_pending=false;

    internal object Inspect()=>new {_pending,_previousPosition,_previousDuration,_previousUpdate,_changedAt};

    internal bool Accept(double position,double duration,DateTimeOffset update,DateTimeOffset now)
    {
        if(!_pending)return true;
        if(update<=_previousUpdate)return false;
        double elapsed=Math.Max(0,(now-_changedAt).TotalSeconds);
        bool reset=position<=elapsed+.35;
        bool differentDuration=Math.Abs(duration-_previousDuration)>.5;
        bool restarted=position<_previousPosition-2;
        bool differentPosition=Math.Abs(position-_previousPosition)>2;
        if(!reset && !restarted && !(differentDuration && differentPosition))return false;
        _pending=false;
        return true;
    }
}
