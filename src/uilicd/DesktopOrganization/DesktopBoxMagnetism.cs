using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.Docking;

namespace GlassDock.DesktopOrganization;

/// <summary>Dock proximity and springs, bounded to each existing icon-grid cell.</summary>
internal sealed class DesktopBoxMagnetism(FrameworkElement viewport,Func<bool> enabled) : IDisposable
{
    private readonly Dictionary<FrameworkElement,Entry> _entries=[];
    private Point? _pointer;
    private bool _animating;
    private double _lastFrame;
    internal bool Animating=>_animating;
    internal sealed record ItemState(string Id,double Scale,double X,double Y,bool Contained);
    internal ItemState[] Inspect()=>_entries.Values.Where(entry=>entry.Tile.IsVisible).Select(entry=>new ItemState(entry.Tile.Tag?.ToString()??"",entry.Scale.Value,entry.X.Value,entry.Y.Value,Contained(entry))).ToArray();
    internal void Register(FrameworkElement tile,Image image,TextBlock label)
    {
        if(_entries.ContainsKey(tile))return;
        var entry=new Entry(tile,image,label); _entries.Add(tile,entry);
        image.RenderTransformOrigin=new Point(.5,.5);
        var transform=new TransformGroup(); transform.Children.Add(entry.Scaling); transform.Children.Add(entry.Offset);
        image.RenderTransform=transform; label.RenderTransform=entry.LabelOffset;
    }
    internal void Remove(FrameworkElement tile) {if(_entries.Remove(tile,out var entry))entry.Reset();}
    internal void PointAt(Point point)
    {
        if(!enabled() || Program.Prefs.ReducedMotion) {Reset(); return;}
        _pointer=point; Start();
    }
    internal void Leave() {_pointer=null; Start();}
    private void Start()
    {
        if(_animating || _entries.Count==0)return;
        _animating=true; _lastFrame=0; CompositionTarget.Rendering+=Animate;
    }
    private void Animate(object? sender,EventArgs args)
    {
        if(args is not RenderingEventArgs frame)return;
        if(!enabled() || !viewport.IsVisible || Program.Prefs.ReducedMotion) {Reset(); return;}
        double now=frame.RenderingTime.TotalSeconds;
        double dt=_lastFrame==0?1d/60:Math.Clamp(now-_lastFrame,0,.033); _lastFrame=now;
        bool moving=false;
        foreach(var entry in _entries.Values)
        {
            var tile=entry.Tile;
            if(!tile.IsVisible || PresentationSource.FromVisual(tile) is null || tile.ActualWidth<4 || tile.ActualHeight<4) {entry.Reset(); continue;}
            var icon=LayoutBounds(entry.Image,tile); var label=LayoutBounds(entry.Label,tile);
            if(icon.Width<1 || icon.Height<1)continue;
            var center=tile.TranslatePoint(new Point(tile.ActualWidth/2,tile.ActualHeight/2),viewport);
            Vector delta=_pointer is { } pointer?center-pointer:new Vector(999,999);
            double weight=_pointer is null?0:DockLayout.Influence(delta.Length);
            // Grid items retain their cell and hit target. Cap to the space above
            // the label so enlargement never clips, overlaps text, or moves a row.
            double bottom=tile.ActualHeight-label.Height-2;
            double limit=Math.Max(1,Math.Min((tile.ActualWidth-4)/icon.Width,(bottom-1)/icon.Height));
            double target=1+Math.Min(Program.Prefs.Magnification,limit-1)*weight;
            moving|=entry.Scale.Advance(target,dt,.30,.0005);
            entry.Scale.Value=Math.Clamp(entry.Scale.Value,1,limit);
            double width=icon.Width*entry.Scale.Value,height=icon.Height*entry.Scale.Value;
            double baseX=icon.X+icon.Width/2,baseY=icon.Y+icon.Height/2;
            double x=Math.Clamp(baseX+Math.Sign(delta.X)*4*weight,2+width/2,Math.Max(2+width/2,tile.ActualWidth-2-width/2))-baseX;
            double y=Math.Clamp(baseY+Math.Sign(delta.Y)*2*weight,1+height/2,Math.Max(1+height/2,bottom-height/2))-baseY;
            if(_pointer is null) x=y=0;
            moving|=entry.X.Advance(x,dt,.28)|entry.Y.Advance(y,dt,.28);
            double labelY=_pointer is null?0:Math.Max(0,baseY+entry.Y.Value+height/2+2-label.Y);
            moving|=entry.LabelY.Advance(labelY,dt,.28);
            entry.Scaling.ScaleX=entry.Scaling.ScaleY=entry.Scale.Value;
            entry.Offset.X=entry.X.Value; entry.Offset.Y=entry.Y.Value; entry.LabelOffset.Y=entry.LabelY.Value;
        }
        if(!moving)Stop();
    }
    private static Rect LayoutBounds(FrameworkElement element,FrameworkElement tile)
    {
        Vector origin=new(); DependencyObject? current=element;
        while(current is Visual visual && current!=tile) {origin+=VisualTreeHelper.GetOffset(visual); current=VisualTreeHelper.GetParent(visual);}
        return new Rect(origin.X,origin.Y,element.ActualWidth,element.ActualHeight);
    }
    private static bool Contained(Entry entry)
    {
        var rect=LayoutBounds(entry.Image,entry.Tile);
        if(rect.Width<1 || rect.Height<1)return true;
        double halfWidth=rect.Width*entry.Scale.Value/2,halfHeight=rect.Height*entry.Scale.Value/2;
        double x=rect.X+rect.Width/2+entry.X.Value,y=rect.Y+rect.Height/2+entry.Y.Value;
        return x-halfWidth>=-.5 && x+halfWidth<=entry.Tile.ActualWidth+.5 && y-halfHeight>=-.5 && y+halfHeight<=entry.Tile.ActualHeight+.5;
    }
    private void Stop() {if(_animating)CompositionTarget.Rendering-=Animate; _animating=false; _lastFrame=0;}
    internal void Reset() {_pointer=null; Stop(); foreach(var entry in _entries.Values)entry.Reset();}
    internal void Clear() {Reset(); _entries.Clear();}
    public void Dispose()=>Clear();
    private sealed class Entry(FrameworkElement tile,Image image,TextBlock label)
    {
        internal readonly FrameworkElement Tile=tile;
        internal readonly Image Image=image;
        internal readonly TextBlock Label=label;
        internal readonly DockSpring Scale=new(1),X=new(),Y=new(),LabelY=new();
        internal readonly ScaleTransform Scaling=new(1,1);
        internal readonly TranslateTransform Offset=new(),LabelOffset=new();
        internal void Reset() {Scale.Snap(1); X.Snap(0); Y.Snap(0); LabelY.Snap(0); Scaling.ScaleX=Scaling.ScaleY=1; Offset.X=Offset.Y=LabelOffset.Y=0;}
    }
}
