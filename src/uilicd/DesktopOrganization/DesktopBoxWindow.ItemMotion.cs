using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Docking;

namespace GlassDock.DesktopOrganization;

/// <summary>Reuses visible icon views and animates their actual screen-space reflow.</summary>
internal sealed partial class DesktopBoxWindow
{
    private readonly Dictionary<string,FrameworkElement> _itemViews=[];
    private readonly Dictionary<FrameworkElement,(DockSpring X,DockSpring Y)> _itemMotion=[];
    private bool _itemAnimating;
    private double _itemLastFrame;
    private int _reflowRevision;
    internal FrameworkElement ItemView(DesktopBoxItem? item)
    {
        if(item is null)return new Border { Width=TileWidth-8,Height=RowHeight-10,Margin=new Thickness(4,5,4,5),CornerRadius=new CornerRadius(11),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromArgb(80,220,239,255)),Background=new SolidColorBrush(Color.FromArgb(12,225,240,255)),IsHitTestVisible=false };
        if(_itemViews.TryGetValue(item.Id,out var old) && old.DataContext is DesktopBoxItem previous && previous!=item)
        { _magnetism.Remove(old); _itemViews.Remove(item.Id); _itemMotion.Remove(old); }
        if(!_itemViews.TryGetValue(item.Id,out var view))
        {
            view=BuildItem(item); view.Tag=item.Id; view.DataContext=item; _itemViews[item.Id]=view;
            // Only realized rows produce views; evict detached views during long scrolls.
            if(_itemViews.Count>160)
                foreach(var key in _itemViews.Where(pair=>VisualTreeHelper.GetParent(pair.Value) is null && pair.Key!=item.Id).Select(pair=>pair.Key).Take(_itemViews.Count-160).ToArray()) {_magnetism.Remove(_itemViews[key]); _itemViews.Remove(key);}
        }
        if(VisualTreeHelper.GetParent(view) is Panel parent)parent.Children.Remove(view);
        view.RenderTransform=new TranslateTransform();
        return view;
    }
    private Dictionary<string,Point> CaptureItemPositions()
    {
        var positions=new Dictionary<string,Point>();
        if(!IsLoaded || !IsVisible)return positions;
        foreach(var pair in _itemViews)
            if(pair.Value.IsVisible && PresentationSource.FromVisual(pair.Value) is not null)
                positions[pair.Key]=pair.Value.PointToScreen(new Point());
        return positions;
    }
    private void AnimateItemReflow(Dictionary<string,Point> positions)
    {
        int revision=++_reflowRevision;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=> {
            if(_closed || !IsVisible || revision!=_reflowRevision)return;
            UpdateLayout();
            var dpi=VisualTreeHelper.GetDpi(this);
            foreach(var pair in _itemViews)
            {
                var view=pair.Value;
                if(!view.IsVisible || PresentationSource.FromVisual(view) is null || !positions.TryGetValue(pair.Key,out var before))continue;
                var offset=view.RenderTransform as TranslateTransform??new TranslateTransform();
                view.RenderTransform=offset;
                var current=view.PointToScreen(new Point());
                double x=offset.X+(before.X-current.X)/dpi.DpiScaleX,y=offset.Y+(before.Y-current.Y)/dpi.DpiScaleY;
                if(Program.Prefs.ReducedMotion) { offset.X=offset.Y=0; _itemMotion.Remove(view); continue; }
                if(Math.Abs(x)<.02 && Math.Abs(y)<.02 && !_itemMotion.ContainsKey(view)) { offset.X=offset.Y=0; continue; }
                // Keep the current visual position even when another insertion interrupts.
                if(_itemMotion.TryGetValue(view,out var previous)) { previous.X.Value=x; previous.Y.Value=y; }
                else _itemMotion[view]=(new DockSpring(x),new DockSpring(y));
                offset.X=x; offset.Y=y;
            }
            if(_itemMotion.Count>0 && !_itemAnimating) { _itemAnimating=true; _itemLastFrame=0; CompositionTarget.Rendering+=AnimateItemFrame; }
        }));
    }
    private void AnimateItemFrame(object? sender,EventArgs args)
    {
        if(args is not RenderingEventArgs frame)return;
        double now=frame.RenderingTime.TotalSeconds,dt=_itemLastFrame==0?1d/60:Math.Clamp(now-_itemLastFrame,0,.033); _itemLastFrame=now;
        foreach(var pair in _itemMotion.ToArray())
        {
            bool moving=pair.Value.X.Advance(0,dt,.28)|pair.Value.Y.Advance(0,dt,.28);
            if(pair.Key.RenderTransform is TranslateTransform offset) { offset.X=pair.Value.X.Value; offset.Y=pair.Value.Y.Value; }
            if(!moving || !pair.Key.IsVisible || Program.Prefs.ReducedMotion)
            { if(pair.Key.RenderTransform is TranslateTransform done)done.X=done.Y=0; _itemMotion.Remove(pair.Key); }
        }
        if(_itemMotion.Count==0 || _closed || !IsVisible)StopItemAnimation();
    }
    private void StopItemAnimation()
    {
        if(_itemAnimating)CompositionTarget.Rendering-=AnimateItemFrame;
        _itemAnimating=false; _itemMotion.Clear();
    }
    private void ReleaseItemViews() { _reflowRevision++; StopItemAnimation(); _magnetism.Clear(); _itemViews.Clear(); }
    internal DesktopBoxMagnetism.ItemState[] InspectMagnetism()=>_magnetism.Inspect();
    internal bool MagnetismAnimating=>_magnetism.Animating;
    internal void PreviewItemMagnetism(int? index)
    {
        if(index is null) {_magnetism.Leave(); return;}
        if(index>=0 && index<_box.Items.Count && _itemViews.TryGetValue(_box.Items[index.Value].Id,out var view))
            _magnetism.PointAt(view.TranslatePoint(new Point(view.ActualWidth/2,view.ActualHeight/2),_items));
    }
}
