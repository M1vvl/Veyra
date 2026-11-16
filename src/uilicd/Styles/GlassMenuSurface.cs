using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDock.Rendering;

namespace GlassDock.Styles;

/// <summary>Popup-local Background → optical Glass → native menu Content.</summary>
public sealed class GlassMenuSurface : Decorator
{
    private GlassRenderer? _renderer;
    private BitmapSource? _frame;
    private readonly DispatcherTimer _timer;
    private bool _presenting;
    private Brush _ink=Brushes.White;
    private double _appliedTransparency=-1;
    public GlassMenuSurface()
    {
        _timer=new DispatcherTimer(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(1000d/30) };
        _timer.Tick+=(_,_)=>RenderMaterial();
        Loaded+=(_,_)=> {
            ApplyInk(Brushes.White);
            if(PresentationSource.FromVisual(this) is HwndSource source) DesktopNative.SetWindowDisplayAffinity(source.Handle,0);
            _timer.Interval=TimeSpan.FromMilliseconds(1000d/Program.Prefs.GlassFramesPerSecond);
            RenderMaterial(); _timer.Start();
        };
        Unloaded+=(_,_)=> { _timer.Stop(); StopPresentation(); _renderer?.Dispose(); _renderer=null; _frame=null; };
    }
    internal bool HasOpticalMaterial=>_frame is not null;
    internal static void Install(Application application)=>application.Resources.MergedDictionaries.Add(new ResourceDictionary {
        Source=new Uri("/uilicd;component/Styles/GlassMenus.xaml",UriKind.Relative) });
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(Math.Max(0,constraint.Width-16),Math.Max(0,constraint.Height-16)));
        return new Size(Math.Max(184,(Child?.DesiredSize.Width??0)+16),(Child?.DesiredSize.Height??0)+16);
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(8,8,Math.Max(0,arrangeSize.Width-16),Math.Max(0,arrangeSize.Height-16)));
        return arrangeSize;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushOpacity(1-Program.Prefs.GlassTransparency);
        if(_frame is not null) drawing.DrawImage(_frame,new Rect(-6,-6,ActualWidth+12,ActualHeight+12));
        else drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(96,32,39,47)),new Pen(new SolidColorBrush(Color.FromArgb(65,255,255,255)),.7),new Rect(0,0,ActualWidth,ActualHeight),17,17);
        drawing.Pop();
    }
    private void RenderMaterial()
    {
        if(!IsLoaded || !IsVisible || ActualWidth<4 || ActualHeight<4 || PresentationSource.FromVisual(this) is null) return;
        ApplyInk(_ink);
        if(_appliedTransparency!=Program.Prefs.GlassTransparency) {_appliedTransparency=Program.Prefs.GlassTransparency; InvalidateVisual();}
        if(Program.Prefs.GlassTransparency>=1) {StopPresentation(); _renderer?.Dispose(); _renderer=null; _frame=null; return;}
        try
        {
            var a=PointToScreen(new Point(-6,-6)); var b=PointToScreen(new Point(ActualWidth+6,ActualHeight+6));
            double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
            _renderer??=new GlassRenderer();
            if(_renderer.TryRender(new Int32Rect((int)Math.Round(a.X),(int)Math.Round(a.Y),Math.Max(1,(int)Math.Round(b.X-a.X)),Math.Max(1,(int)Math.Round(b.Y-a.Y))),
                GlassMaterialProfile.Apply(new GlassOptions { CornerRadiusPixels=(float)(17*dpi),SurfaceInsetPixels=(float)(7*dpi),ThicknessPixels=(float)(8*dpi),NormalTransitionPixels=(float)(12.5*dpi),BackgroundDistancePixels=(float)(24*dpi),Tint=.035f }),out var frame) && frame is not null)
                if(_renderer.LastFrameUpdated || frame!=_frame) Present(frame);
            if(_renderer.HasPendingFrame && !_presenting) { _presenting=true; CompositionTarget.Rendering+=PresentPending; }
        }
        catch(Exception exception) { Program.Log("Glass menu: "+exception.Message); }
    }
    private void PresentPending(object? sender,EventArgs e)
    {
        if(!IsVisible || _renderer is null) { StopPresentation(); return; }
        if(_renderer.TryPresentPending(out var frame) && frame is not null) Present(frame);
        if(!_renderer.HasPendingFrame) StopPresentation();
    }
    private void Present(BitmapSource frame)
    {
        _frame=frame;
        ApplyInk(MaterialContrast.SampleLuminance(frame)<.48?Brushes.White:Ui.Ink);
        InvalidateVisual();
    }
    private void ApplyInk(Brush ink)
    {
        _ink=ink; TextElement.SetForeground(this,ink);
        void Walk(DependencyObject node)
        {
            // Theme MenuItem foreground values outrank inheritance across a Popup.
            if(node is MenuItem menu)menu.Foreground=ink;
            if(node is TextBlock text)text.Foreground=ink;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(node);index++)Walk(VisualTreeHelper.GetChild(node,index));
        }
        if(Child is not null)Walk(Child);
    }
    private void StopPresentation() { if(!_presenting) return; _presenting=false; CompositionTarget.Rendering-=PresentPending; }
}
