using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Rendering;

namespace GlassDock;

/// <summary>
/// A regional optical surface driven by its GlassWindow's shared render/present cadence.
/// Background visuals do not participate in measurement, and content remains the last layer.
/// </summary>
internal sealed class GlassCard : Grid, IDisposable
{
    private readonly Canvas _background = new() { IsHitTestVisible = false };
    private readonly Border _fallback = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(34, 225, 238, 250)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 240, 248, 255)),
        BorderThickness = new Thickness(.6),
        IsHitTestVisible = false
    };
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private const double ShadowPadding = 6;
    private readonly ContentPresenter _content = new();
    private GlassRenderer? _renderer;
    private GlassWindow? _host;
    private bool _disposed;
    private double _lastLuminance = double.NaN;
    private string? _lastError;

    internal UIElement? Content
    {
        get => _content.Content as UIElement;
        set => _content.Content = value;
    }

    internal double CornerRadius { get; set; } = 16;
    internal float Tint { get; set; } = .025f;
    internal bool HasPendingFrame => _renderer?.HasPendingFrame == true;
    internal bool HasOpticalMaterial => _image.Source is not null && _fallback.Visibility == Visibility.Hidden;
    internal void ApplyTransparency()=>_background.Opacity=1-Program.Prefs.GlassTransparency;
    internal event Action<double>? LuminanceChanged;

    internal GlassCard()
    {
        _background.Children.Add(_fallback);
        _background.Children.Add(_image);
        Children.Add(_background);
        Children.Add(_content);
        Loaded += (_, _) =>
        {
            if (_disposed) return;
            _host = Window.GetWindow(this) as GlassWindow;
            ApplyTransparency();
            _host?.RegisterGlassCard(this);
        };
        Unloaded += (_, _) =>
        {
            _host?.UnregisterGlassCard(this);
            _host = null;
            ReleaseMaterial();
        };
        SizeChanged += (_, e) =>
        {
            _fallback.Width = e.NewSize.Width;
            _fallback.Height = e.NewSize.Height;
            _image.Width = e.NewSize.Width+2*ShadowPadding;
            _image.Height = e.NewSize.Height+2*ShadowPadding;
            Canvas.SetLeft(_image,-ShadowPadding);
            Canvas.SetTop(_image,-ShadowPadding);
            _fallback.CornerRadius = new CornerRadius(CornerRadius);
        };
    }

    internal void RenderMaterial()
    {
        if (!CanRender())
        {
            ReleaseMaterial(_disposed);
            return;
        }

        try
        {
            Point start = PointToScreen(new Point(-ShadowPadding,-ShadowPadding));
            Point end = PointToScreen(new Point(ActualWidth+ShadowPadding, ActualHeight+ShadowPadding));
            var region = new Int32Rect(
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y),
                Math.Max(1, (int)Math.Round(end.X - start.X)),
                Math.Max(1, (int)Math.Round(end.Y - start.Y)));
            float scale = (float)Math.Min(region.Width / (ActualWidth+2*ShadowPadding), region.Height / (ActualHeight+2*ShadowPadding));
            var options = new GlassOptions
            {
                CornerRadiusPixels = (float)CornerRadius * scale,
                SurfaceInsetPixels = (.8f+(float)ShadowPadding) * scale,
                ThicknessPixels = 6.2f * scale,
                NormalTransitionPixels = 10f * scale,
                BackgroundDistancePixels = 20f * scale,
                Tint = Tint
            };

            _renderer ??= new GlassRenderer();
            if (_renderer.TryRender(region, GlassMaterialProfile.Apply(options), out BitmapSource? frame) && frame is not null)
            {
                if (_renderer.LastFrameUpdated || _image.Source != frame) Present(frame);
            }
            else if (_renderer.LastError is not null)
            {
                ShowFallback();
            }
        }
        catch (InvalidOperationException exception)
        {
            // A section can unload between layout and its queued material update.
            ShowFallback(exception.Message);
        }
    }

    internal void PresentPendingMaterial()
    {
        if (!CanRender())
        {
            ReleaseMaterial(_disposed);
            return;
        }

        if (_renderer is null) return;
        if (_renderer.TryPresentPending(out BitmapSource? frame) && frame is not null)
            Present(frame);
        else if (_renderer.LastError is not null)
            ShowFallback();
    }

    private bool CanRender()
    {
        if (_disposed || Program.Prefs.GlassTransparency>=1 || !IsLoaded || !IsVisible ||
            ActualWidth < 4 || ActualHeight < 4 || PresentationSource.FromVisual(this) is null)
            return false;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(this);
             ancestor is Visual;
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is not FrameworkElement element) continue;
            if (!element.IsVisible) return false;
            if (!element.ClipToBounds && element is not ScrollContentPresenter && element.Clip is null)
                continue;

            Rect clip = new(0, 0, element.ActualWidth, element.ActualHeight);
            if (element.Clip is not null) clip.Intersect(element.Clip.Bounds);
            Rect transformed = TransformToAncestor(element).TransformBounds(bounds);
            if (!clip.IntersectsWith(transformed)) return false;
        }

        return true;
    }

    private void Present(BitmapSource frame)
    {
        _image.Source = frame;
        _fallback.Visibility = Visibility.Hidden;
        NotifyLuminance(MaterialContrast.SampleLuminance(frame));
    }

    private void NotifyLuminance(double value)
    {
        bool contrastChanged = (_lastLuminance < .48) != (value < .48);
        if (!double.IsNaN(_lastLuminance) && !contrastChanged && Math.Abs(value - _lastLuminance) < .04)
            return;
        _lastLuminance = value;
        LuminanceChanged?.Invoke(value);
    }

    private void ShowFallback(string? error = null)
    {
        if(_renderer?.IsTemporarilyUnavailable==true && _image.Source is not null) return;
        _image.Source = null;
        _fallback.Visibility = Visibility.Visible;
        NotifyLuminance(.85);
        error ??= _renderer?.LastError;
        if (error is null || error == _lastError) return;
        _lastError = error;
        Program.Log(error);
    }

    internal void ReleaseMaterial(bool clearImage=false)
    {
        if(clearImage)
        {
            _renderer?.Dispose();
            _renderer=null;
            _image.Source = null;
            _fallback.Visibility = Visibility.Visible;
            NotifyLuminance(.85);
        }
        else _renderer?.SuspendResources();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host?.UnregisterGlassCard(this);
        _host = null;
        ReleaseMaterial(true);
    }
}
