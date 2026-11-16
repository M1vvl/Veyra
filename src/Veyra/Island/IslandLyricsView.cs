using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GlassDock.Island;

internal sealed class IslandLyricsView : ScrollViewer
{
    private static readonly DependencyProperty ScrollPositionProperty=DependencyProperty.Register(
        "ScrollPosition",typeof(double),typeof(IslandLyricsView),
        new PropertyMetadata(0d,(owner,args)=>((IslandLyricsView)owner).ScrollToVerticalOffset((double)args.NewValue)));
    private Brush _activeBrush=IslandPalette.Accent(IslandAccentColor.Default);
    private static readonly DropShadowEffect ActiveShadow=CreateActiveShadow();
    private TextBlock? _current;
    private bool _centerPending,_animatePending;
    private int _scrollRevision;
    internal StackPanel Lines {get;}=new();

    internal IslandLyricsView()
    {
        VerticalScrollBarVisibility=ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        Content=Lines;
        var fade=new LinearGradientBrush
        {
            StartPoint=new Point(0,0),EndPoint=new Point(0,1),
            GradientStops=new GradientStopCollection
            {
                new(Colors.Transparent,0),new(Colors.Black,.32),
                new(Colors.Black,.68),new(Colors.Transparent,1)
            }
        };
        fade.Freeze();
        OpacityMask=fade;
        ScrollChanged+=(_,args)=> {
            if(args.ViewportHeightChange!=0 || args.ViewportWidthChange!=0 || args.ExtentHeightChange!=0)
                RequestCenter(false);
        };
        Loaded+=(_,_)=>RequestCenter(false);
        Unloaded+=(_,_)=> {++_scrollRevision;BeginAnimation(ScrollPositionProperty,null);};
    }

    internal void SelectLine(TextBlock? line)
    {
        if(_current is not null)
        {
            _current.FontWeight=FontWeights.Normal;
            _current.Foreground=Brushes.White;
            _current.Opacity=.44;
            _current.RenderTransform=Transform.Identity;
            _current.Effect=null;
        }
        _current=line;
        if(line is null)return;
        line.FontWeight=FontWeights.SemiBold;
        line.Foreground=_activeBrush;
        line.Opacity=1;
        var scale=new ScaleTransform(1.05,1.05);
        scale.Freeze();
        line.RenderTransformOrigin=new Point(.5,.5);
        line.RenderTransform=scale;
        line.Effect=ActiveShadow;
        RequestCenter(true);
    }

    private void RequestCenter(bool animate)
    {
        _animatePending|=animate;
        if(_centerPending || !IsLoaded)return;
        _centerPending=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=> {
            _centerPending=false;
            bool shouldAnimate=_animatePending;
            _animatePending=false;
            CenterCurrent(shouldAnimate);
        }));
    }

    private void CenterCurrent(bool animate)
    {
        if(!IsLoaded || _current is null || !Lines.Children.Contains(_current) || ViewportHeight<=0)return;
        // End padding lets the first and last lines reach the same viewport center.
        var padding=new Thickness(8,ViewportHeight/2,8,ViewportHeight/2);
        if(Lines.Margin!=padding)
        {
            Lines.Margin=padding;
            RequestCenter(animate);
            return;
        }
        double center=_current.TranslatePoint(new Point(0,_current.ActualHeight/2),this).Y;
        double target=Math.Clamp(VerticalOffset+center-ViewportHeight/2,0,ScrollableHeight);
        if(Math.Abs(target-VerticalOffset)<.25)return;
        int revision=++_scrollRevision;
        if(!animate || Program.Prefs.ReducedMotion)
        {
            BeginAnimation(ScrollPositionProperty,null);
            SetValue(ScrollPositionProperty,target);
            ScrollToVerticalOffset(target);
            return;
        }
        var motion=new DoubleAnimation(VerticalOffset,target,TimeSpan.FromMilliseconds(200))
        {
            EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}
        };
        motion.Completed+=(_,_)=> {
            if(revision!=_scrollRevision)return;
            BeginAnimation(ScrollPositionProperty,null);
            SetValue(ScrollPositionProperty,target);
            ScrollToVerticalOffset(target);
        };
        BeginAnimation(ScrollPositionProperty,motion,HandoffBehavior.SnapshotAndReplace);
    }

    internal void SetAccent(IslandAccentColor color)
    {
        _activeBrush=IslandPalette.Accent(color);
        if(_current is not null)_current.Foreground=_activeBrush;
    }

    private static DropShadowEffect CreateActiveShadow()
    {
        var shadow=new DropShadowEffect {Color=Colors.Black,BlurRadius=4,ShadowDepth=1.5,Opacity=.75,Direction=270};
        shadow.Freeze();
        return shadow;
    }
}
