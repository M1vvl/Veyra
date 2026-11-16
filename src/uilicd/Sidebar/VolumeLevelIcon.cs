using System.Windows;
using System.Windows.Media;

namespace GlassDock.Sidebar;

/// <summary>Three independent wave bands; inactive bands remain faint and visible.</summary>
public sealed class VolumeLevelIcon : FrameworkElement
{
    public static readonly DependencyProperty InkProperty=DependencyProperty.Register(nameof(Ink),typeof(Brush),typeof(VolumeLevelIcon),new FrameworkPropertyMetadata(Brushes.White,FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink { get=>(Brush)GetValue(InkProperty); set=>SetValue(InkProperty,value); }
    private int _bands;
    private bool _muted,_available;
    internal int ActiveBands=>_bands;
    internal static int Bands(double volume,bool muted,bool available=true)
    {
        if(!available || muted || !double.IsFinite(volume) || volume<=0)return 0;
        // Endpoint volume arrives as float32; tolerate its rounding at exact thirds.
        return volume<=1d/3+1e-7?1:volume<=2d/3+1e-7?2:3;
    }
    internal void Update(Services.AudioState state)
    {
        System.Windows.Automation.AutomationProperties.SetName(this,!state.Available?"音量不可用":state.Muted?"已静音":$"音量 {Math.Round(state.Volume*100)}%");
        int bands=Bands(state.Volume,state.Muted,state.Available);
        if(bands==_bands && state.Muted==_muted && state.Available==_available)return;
        _bands=bands; _muted=state.Muted; _available=state.Available;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        double scale=Math.Min(ActualWidth/28,ActualHeight/28);
        drawing.PushTransform(new TranslateTransform((ActualWidth-28*scale)/2,(ActualHeight-28*scale)/2));
        drawing.PushTransform(new ScaleTransform(scale,scale));
        var pen=new Pen(Ink,1.5) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        drawing.PushOpacity(_available?1:.3);
        var speaker=new StreamGeometry();
        using(var path=speaker.Open())
        {
            path.BeginFigure(new Point(3,11),false,true); path.PolyLineTo(new[]{new Point(7,11),new Point(12,7),new Point(12,21),new Point(7,17),new Point(3,17)},true,true);
        }
        speaker.Freeze(); drawing.DrawGeometry(null,pen,speaker);
        for(int band=0;band<3;band++)
        {
            double radius=4+band*3.4;
            var wave=new StreamGeometry();
            using(var path=wave.Open())
            {
                path.BeginFigure(new Point(13+radius*.65,14-radius*.72),false,false);
                path.ArcTo(new Point(13+radius*.65,14+radius*.72),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);
            }
            wave.Freeze(); drawing.PushOpacity(band<_bands?1:.18); drawing.DrawGeometry(null,pen,wave); drawing.Pop();
        }
        if(_muted) { drawing.DrawLine(pen,new Point(17,12),new Point(22,17)); drawing.DrawLine(pen,new Point(22,12),new Point(17,17)); }
        drawing.Pop(); drawing.Pop(); drawing.Pop();
    }
}
