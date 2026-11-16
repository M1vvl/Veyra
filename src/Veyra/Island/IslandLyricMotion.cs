using System;

namespace GlassDock.Island;

// This clock belongs to the island display. Player observations never drive transforms directly.
internal sealed class IslandPlaybackClock
{
    private bool _ready,_playing,_pendingJump;
    private double _position,_at,_rate,_jumpPosition,_jumpAt;
    internal int Revision {get;private set;}

    internal void Reset()
    {
        _ready=false;
        _pendingJump=false;
        Revision++;
    }

    internal double Read(double now)
    {
        if(!_ready)return 0;
        // Hold after a prolonged loss of observations instead of inventing playback indefinitely.
        return _position+(_playing?Math.Clamp(now-_at,0,1)*_rate:0);
    }

    internal void Observe(double position,bool playing,double now,bool seek=false)
    {
        if(!double.IsFinite(position) || position<0 || !double.IsFinite(now))return;
        double predicted=Read(now);
        double error=position-predicted;
        if(!_ready || seek)
        {
            Anchor(position,playing,now);
            Revision++;
            return;
        }
        if(Math.Abs(error)>.75)
        {
            // Reject transient zero/stale samples. External seeks need a consistent
            // short sequence; explicit island seeks use the immediate path above.
            double jumpElapsed=now-_jumpAt;
            bool consistent=_pendingJump && jumpElapsed>=0 && jumpElapsed<.25
                && Math.Abs(position-(_jumpPosition+(playing?jumpElapsed:0)))<.25;
            if(consistent)
            {
                if(jumpElapsed>=.06)
                {
                    Anchor(position,playing,now);
                    Revision++;
                }
                return;
            }
            _pendingJump=true;
            _jumpPosition=position;
            _jumpAt=now;
            return;
        }
        _pendingJump=false;
        _position=predicted;
        _at=now;
        _playing=playing;
        // Correct small jitter by varying forward speed, preserving a continuous position.
        _rate=playing?Math.Clamp(1+error*2,.7,1.3):0;
    }

    private void Anchor(double position,bool playing,double now)
    {
        _ready=true;
        _pendingJump=false;
        _position=position;
        _at=now;
        _playing=playing;
        _rate=playing?1:0;
    }
}
