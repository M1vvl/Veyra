namespace GlassDock.Docking;

internal sealed class DockDragState
{
    internal bool External;
    internal bool Over;
    internal string? PinId;
    internal int Boundary=2;
    internal double Proximity;
    internal double PointerX,PointerY,GrabX,GrabY;
    internal void Leave() { Over=false; Proximity=0; }
    internal void End() { Leave(); External=false; PinId=null; }
}
