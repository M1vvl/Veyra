using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDock.Rendering;

namespace GlassDock;

internal abstract class GlassWindow : Window
{
    protected readonly Canvas Root = new();
    protected readonly Border Fallback = new() { Background=new SolidColorBrush(Color.FromArgb(224,231,238,246)), IsHitTestVisible=false };
    protected readonly Image Glass = new() { Stretch=Stretch.Fill, IsHitTestVisible=false };
    protected readonly Grid ContentLayer = new();
    private const double ShadowPadding = 6;
    private readonly DispatcherTimer _glassTimer;
    private readonly HashSet<GlassCard> _glassCards=[];
    internal int GlassCardCount=>_glassCards.Count;
    internal int OpticalGlassCardCount=>_glassCards.Count(card=>card.HasOpticalMaterial);
    internal bool HasMaterialResources=>_renderer is not null;
    internal bool HasMaterialImage=>Glass.Source is not null;
    internal double GlassLayerOpacity=>Glass.Opacity;
    internal double ContentLayerOpacity=>ContentLayer.Opacity;
    internal Color MaterialCenterColor
    {
        get
        {
            if(Glass.Source is not BitmapSource image)return Colors.Transparent;
            byte[] pixel=new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth/2,image.PixelHeight/2,1,1),pixel,4,0);
            return Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
        }
    }
    internal long MaterialAllocationGeneration=>_renderer?.AllocationGeneration??0;
    internal object InspectMaterialState()
    {
        byte[] pixel=new byte[4];
        if(Glass.Source is BitmapSource image && image.PixelWidth>0 && image.PixelHeight>0)
            image.CopyPixels(new Int32Rect(image.PixelWidth/2,image.PixelHeight/2,1,1),pixel,4,0);
        return new {Title,IsVisible,SurfaceVisible,hasImage=Glass.Source is not null,red=pixel[2],green=pixel[1],blue=pixel[0],alpha=pixel[3],error=_renderer?.LastError};
    }
    private GlassRenderer? _renderer;
    protected Rect Surface;
    protected double Radius=24;
    private bool _surfaceVisible=true;
    protected bool SurfaceVisible
    {
        get=>_surfaceVisible;
        set
        {
            if(_surfaceVisible==value)return; _surfaceVisible=value;
            if(!value)_glassTimer?.Stop();
            else if(IsLoaded && IsVisible)_glassTimer?.Start();
        }
    }
    protected double ContentScale=1;
    private string? _lastRenderError;
    private bool _presenting;
    private TimeSpan _lastPresentationTime=TimeSpan.MinValue;
    protected virtual bool NoActivate => true;
    protected GlassWindow(string title)
    {
        Title=title; Icon=Branding.WindowIcon; WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=Brushes.Transparent;
        ShowInTaskbar=false; Topmost=true; ResizeMode=ResizeMode.NoResize; ShowActivated=false;
        UseLayoutRounding=true; SnapsToDevicePixels=true; Content=Root;
        Root.Children.Add(Fallback); Root.Children.Add(Glass); Root.Children.Add(ContentLayer);
        SourceInitialized+=(_,_)=> {
            DesktopNative.Configure(this,NoActivate);
            var source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WndProc);
        };
        _glassTimer=new DispatcherTimer(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(1000d/Program.Prefs.GlassFramesPerSecond) };
        _glassTimer.Tick+=(_,_)=>RenderGlass();
        Loaded+=(_,_)=> { ApplyPreferences(); RenderGlass(); if(CanRenderMaterial)_glassTimer.Start(); };
        IsVisibleChanged+=(_,_)=> {
            if(!IsVisible) { _glassTimer.Stop(); ReleaseMaterial(); }
            else if(IsLoaded && SurfaceVisible)_glassTimer.Start();
        };
        Closed+=(_,_)=> {
            _glassTimer.Stop(); ReleaseMaterial(true);
            foreach(var card in _glassCards.ToArray()) card.Dispose();
            _glassCards.Clear();
        };
    }
    protected void SetSurface(Rect rect, double radius)
    {
        Surface=rect; Radius=radius;
        foreach(var e in new FrameworkElement[]{ Fallback, Glass, ContentLayer })
        {
            Canvas.SetLeft(e,rect.Left); Canvas.SetTop(e,rect.Top); e.Width=rect.Width; e.Height=rect.Height;
        }
        Canvas.SetLeft(Glass,rect.Left-ShadowPadding);
        Canvas.SetTop(Glass,rect.Top-ShadowPadding);
        Glass.Width=rect.Width+2*ShadowPadding;
        Glass.Height=rect.Height+2*ShadowPadding;
        Fallback.CornerRadius=new CornerRadius(radius);
        if(Glass.Source is null) Fallback.Visibility=Visibility.Visible;
        // Subclasses establish their surface after the base Loaded handler.
        // Start only once valid dimensions exist, including a resumed surface.
        if(CanRenderMaterial && !_glassTimer.IsEnabled)_glassTimer.Start();
    }
    public virtual void ApplyPreferences()
    {
        Glass.Opacity=Fallback.Opacity=1-Program.Prefs.GlassTransparency;
        foreach(var card in _glassCards)card.ApplyTransparency();
        _glassTimer.Interval=TimeSpan.FromMilliseconds(1000d/Program.Prefs.GlassFramesPerSecond);
        if(!Program.Prefs.GlassEnabled || Program.Prefs.GlassTransparency>=1) {_glassTimer.Stop(); ReleaseMaterial(true);}
        else if(CanRenderMaterial && !_glassTimer.IsEnabled)_glassTimer.Start();
    }
    protected void ReleaseMaterial(bool clearImage=false)
    {
        StopPresentation();
        if(clearImage)
        {
            _renderer?.Dispose(); _renderer=null;
            Glass.Source=null; Fallback.Visibility=Visibility.Visible; OnMaterialLuminance(.85);
        }
        else _renderer?.SuspendResources();
        foreach(var card in _glassCards) card.ReleaseMaterial(clearImage);
    }
    internal void RegisterGlassCard(GlassCard card)
    {
        if(!_glassCards.Add(card) || !CanRenderMaterial) return;
        card.RenderMaterial();
        UpdatePresentation();
    }
    internal void UnregisterGlassCard(GlassCard card)
    {
        if(!_glassCards.Remove(card)) return;
        card.ReleaseMaterial();
        UpdatePresentation();
    }
    protected virtual void OnMaterialLuminance(double luminance) { }
    protected void RenderGlass()
    {
        if(!CanRenderMaterial) { ReleaseMaterial(!Program.Prefs.GlassEnabled); return; }
        try
        {
            _renderer??=new GlassRenderer();
            var materialBounds=Surface;
            materialBounds.Inflate(ShadowPadding,ShadowPadding);
            var start=Root.PointToScreen(materialBounds.TopLeft); var end=Root.PointToScreen(materialBounds.BottomRight);
            var region=new Int32Rect((int)Math.Round(start.X),(int)Math.Round(start.Y),Math.Max(1,(int)Math.Round(end.X-start.X)),Math.Max(1,(int)Math.Round(end.Y-start.Y)));
            var dpi=VisualTreeHelper.GetDpi(this).DpiScaleX*ContentScale;
            var options=new GlassOptions {
                CornerRadiusPixels=(float)(Radius*dpi),
                SurfaceInsetPixels=(float)((2+ShadowPadding)*dpi),
                ThicknessPixels=(float)(8*dpi),
                NormalTransitionPixels=(float)(12.5*dpi),
                BackgroundDistancePixels=(float)(24*dpi),
                Tint=.035f
            };
            if(_renderer.TryRender(region,options,out var image) && image is not null)
            {
                if(_renderer.LastFrameUpdated || Glass.Source!=image) PresentFrame(image);
            }
            else
            {
                if(_renderer.LastError is not null) ShowMaterialFallback();
            }
        }
        catch(Exception ex) { if(ex.Message!=_lastRenderError) { Program.Log(ex.ToString()); _lastRenderError=ex.Message; } }
        foreach(var card in _glassCards) card.RenderMaterial();
        UpdatePresentation();
    }
    private bool CanRenderMaterial => IsLoaded && IsVisible && SurfaceVisible && Program.Prefs.GlassEnabled && Program.Prefs.GlassTransparency<1 && Surface.Width>=1 && Surface.Height>=1;
    private void PresentFrame(BitmapSource image)
    {
        Glass.Source=image; Fallback.Visibility=Visibility.Hidden;
        OnMaterialLuminance(MaterialContrast.SampleLuminance(image));
    }
    private void ShowMaterialFallback()
    {
        if(_renderer?.IsTemporarilyUnavailable==true && Glass.Source is not null) return;
        Glass.Source=null; Fallback.Visibility=Visibility.Visible; OnMaterialLuminance(.85);
        if(_renderer?.LastError is { } error && error!=_lastRenderError) { Program.Log(error); _lastRenderError=error; }
    }
    private void UpdatePresentation()
    {
        bool pending=_renderer?.HasPendingFrame==true || _glassCards.Any(card=>card.HasPendingFrame);
        if(!pending || !CanRenderMaterial) { StopPresentation(); return; }
        if(_presenting) return;
        _presenting=true;
        CompositionTarget.Rendering+=PresentPendingFrame;
    }
    private void StopPresentation()
    {
        if(!_presenting) return;
        _presenting=false;
        CompositionTarget.Rendering-=PresentPendingFrame;
    }
    private void PresentPendingFrame(object? sender,EventArgs e)
    {
        if(!CanRenderMaterial) { ReleaseMaterial(!Program.Prefs.GlassEnabled); return; }
        if(e is RenderingEventArgs rendering)
        {
            if(rendering.RenderingTime==_lastPresentationTime) return;
            _lastPresentationTime=rendering.RenderingTime;
        }
        if(_renderer is not null)
        {
            if(_renderer.TryPresentPending(out var image) && image is not null) PresentFrame(image);
            else if(_renderer.LastError is not null) ShowMaterialFallback();
        }
        foreach(var card in _glassCards) card.PresentPendingMaterial();
        UpdatePresentation();
    }
    protected virtual nint WndProc(nint hwnd,int msg,nint wParam,nint lParam,ref bool handled)
    {
        if(msg is 0x007E or 0x02E0) Dispatcher.BeginInvoke(new Action(Reposition));
        return 0;
    }
    protected abstract void Reposition();
}
