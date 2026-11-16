using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock.Island;

internal sealed class IslandSeekBar : FrameworkElement
{
    private long _lastRequest;
    internal double Progress {get;set;}
    internal bool Dragging {get;private set;}
    internal event Action<double>? SeekRequested;
    internal IslandSeekBar()
    {
        Height=10; Cursor=Cursors.Hand;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(this,true);
        MouseLeftButtonDown+=(_,e)=> {Dragging=true; CaptureMouse(); UpdatePointer(e); e.Handled=true;};
        MouseMove+=(_,e)=>
        {
            if(!Dragging)return;
            UpdatePointer(e);
            long now=Environment.TickCount64;
            if(now-_lastRequest<25)return;
            _lastRequest=now;
            SeekRequested?.Invoke(Progress);
        };
        MouseLeftButtonUp+=(_,e)=>
        {
            if(!Dragging)return;
            UpdatePointer(e); Dragging=false; ReleaseMouseCapture(); SeekRequested?.Invoke(Progress); e.Handled=true;
        };
        LostMouseCapture+=(_,_)=>Dragging=false;
    }
    private void UpdatePointer(MouseEventArgs e)
    {
        Progress=Math.Clamp(e.GetPosition(this).X/Math.Max(1,ActualWidth),0,1); InvalidateVisual();
    }
    private static readonly Brush Track=FrozenBrush(Color.FromArgb(72,146,165,181));
    private Brush _fill=IslandPalette.Accent(IslandAccentColor.Default);
    internal Brush Accent=>_fill;
    internal void SetAccent(IslandAccentColor color)
    {
        var brush=IslandPalette.Accent(color);
        if(ReferenceEquals(_fill,brush))return;
        _fill=brush;
        InvalidateVisual();
    }
    private static Brush FrozenBrush(Color color)
    {
        var brush=new SolidColorBrush(color);brush.Freeze();return brush;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        double y=(ActualHeight-2)/2;
        drawing.DrawRoundedRectangle(Track,null,new Rect(0,y,ActualWidth,2),1,1);
        drawing.DrawRoundedRectangle(_fill,null,new Rect(0,y,ActualWidth*Math.Clamp(Progress,0,1),2),1,1);
    }
}
