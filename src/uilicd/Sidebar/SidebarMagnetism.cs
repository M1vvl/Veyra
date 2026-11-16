using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassDock.Docking;

namespace GlassDock.Sidebar;

/// <summary>Vertical Dock motion. Layout is sampled once; live feedback uses transforms.</summary>
internal sealed class SidebarMagnetism(StackPanel host)
{
    private readonly List<Item> _items=[];
    private double _pointer=double.NaN;
    internal double Expansion { get; private set; }
    internal bool Active=>double.IsFinite(_pointer);

    private void EnsureLayout()
    {
        bool changed=_items.Count!=host.Children.Count;
        for(int i=0;!changed && i<_items.Count;i++)
        {
            var item=_items[i]; var element=item.Element;
            var slot=LayoutInformation.GetLayoutSlot(element);
            changed=!ReferenceEquals(element,host.Children[i])
                || Math.Abs(item.Center-(slot.Top+element.Margin.Top+element.ActualHeight/2))>.01
                || Math.Abs(item.Height-element.ActualHeight)>.01 || Math.Abs(item.Width-element.ActualWidth)>.01;
        }
        if(changed) RefreshLayout();
    }

    internal void RefreshLayout()
    {
        _items.Clear();
        foreach(FrameworkElement element in host.Children)
        {
            var slot=LayoutInformation.GetLayoutSlot(element);
            bool interactive=element is Button || element is Border { Child: not null };
            var item=new Item(element,slot.Top+element.Margin.Top+element.ActualHeight/2,interactive);
            item.ScaleTransform.CenterX=element.ActualWidth/2;
            item.ScaleTransform.CenterY=element.ActualHeight/2;
            var transform=new TransformGroup();
            transform.Children.Add(item.ScaleTransform); transform.Children.Add(item.Translation);
            element.RenderTransform=transform;
            _items.Add(item);
        }
        Expansion=0;
    }

    internal bool PointAt(double y)
    {
        bool changed=!double.IsFinite(_pointer) || Math.Abs(y-_pointer)>.1;
        _pointer=y; return changed;
    }
    internal bool Leave() { bool changed=Active; _pointer=double.NaN; return changed; }
    internal bool Update(double elapsed,bool reduced,double strength)
    {
        EnsureLayout();
        bool moving=false;
        double total=0;
        foreach(var item in _items)
        {
            double distance=double.IsFinite(_pointer)?Math.Abs(_pointer-item.Center):double.PositiveInfinity;
            double influence=Math.Pow(Math.Cos(Math.Min(1,distance/138)*Math.PI*.5),2);
            double target=1+(!reduced && item.Interactive?Math.Clamp(strength,0,.2)*influence:0);
            if(reduced) item.Scale.Snap(1);
            else moving|=item.Scale.Advance(target,elapsed,.30,.0005);
            if(item.Scale.Value>1.2) item.Scale.Snap(1.2);
            else if(item.Scale.Value<1) item.Scale.Snap(1);
            item.Extra=item.Element.ActualHeight*(item.Scale.Value-1);
            total+=item.Extra;
        }
        double before=0;
        foreach(var item in _items)
        {
            item.ScaleTransform.ScaleX=item.ScaleTransform.ScaleY=item.Scale.Value;
            item.Translation.Y=before+item.Extra/2-total/2;
            before+=item.Extra;
        }
        Expansion=total;
        return moving;
    }

    internal double RequiredWidth=>_items.Count==0?0:_items.Max(item=>item.Width*item.Scale.Value)+16;

    internal object Inspect()=>new { Active,Expansion,Items=_items.Select(item=>new {
        Center=item.Center+item.Translation.Y,Height=item.Element.ActualHeight*item.Scale.Value,
        Scale=item.Scale.Value,item.Interactive }).ToArray() };

    private sealed class Item(FrameworkElement element,double center,bool interactive)
    {
        internal readonly FrameworkElement Element=element;
        internal readonly double Center=center;
        internal readonly bool Interactive=interactive;
        internal readonly double Height=element.ActualHeight,Width=element.ActualWidth;
        internal readonly DockSpring Scale=new(1);
        internal readonly ScaleTransform ScaleTransform=new(1,1);
        internal readonly TranslateTransform Translation=new();
        internal double Extra;
    }
}
