using System.Windows;

namespace GlassDock.Sidebar;

/// <summary>Recognizes a leftward mode pull; never changes the rail's window position.</summary>
internal sealed class SidebarPullGesture
{
    private Point _start;
    internal bool Tracking { get; private set; }
    internal bool Dragging { get; private set; }
    internal bool Ready { get; private set; }
    internal void Begin(Point point) { _start=point; Tracking=true; Dragging=Ready=false; }
    internal void Update(Point point)
    {
        if(!Tracking) return;
        var delta=point-_start;
        Dragging|=delta.Length>=4;
        // Short pull plus a release dead band keeps the final mouse-up from
        // losing a deliberate gesture to a few pixels of hand jitter.
        double threshold=Ready?12:18;
        Ready=double.IsFinite(delta.X) && double.IsFinite(delta.Y) && -delta.X>=threshold && -delta.X>=Math.Abs(delta.Y)*1.2;
    }
    internal (bool Dragged,bool Toggle) End(bool cancelled=false)
    {
        var result=(Dragging,Tracking && Ready && !cancelled);
        Tracking=Dragging=Ready=false;
        return result;
    }
}
