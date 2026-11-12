namespace GlassDock.Docking;

/// <summary>Critically damped, interruptible motion with velocity retained when retargeted.</summary>
internal sealed class DockSpring(double value=0)
{
    internal double Value=value;
    internal double Velocity;

    internal bool Advance(double target,double elapsed,double response=.28,double tolerance=.02)
    {
        double omega=2*Math.PI/response,delta=Value-target,c=Velocity+omega*delta,decay=Math.Exp(-omega*elapsed);
        Value=target+(delta+c*elapsed)*decay;
        Velocity=(Velocity-omega*c*elapsed)*decay;
        bool moving=Math.Abs(Value-target)>tolerance || Math.Abs(Velocity)>tolerance*8;
        if(!moving) Snap(target);
        return moving;
    }

    internal void Snap(double value) { Value=value; Velocity=0; }
    internal void Follow(double value,double elapsed)
    {
        Velocity=elapsed>0?Math.Clamp((value-Value)/elapsed,-1800,1800):0;
        Value=value;
    }
}
