using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace GlassDock;

internal static class ControlStyles
{
    internal static readonly Style Slider = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Setter Property="Height" Value="28"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Slider">
                <Grid Margin="1">
                  <Border x:Name="rail" Background="#182D3D50" CornerRadius="13"
                          BorderBrush="#38FFFFFF" BorderThickness="0.7"/>
                  <ProgressBar x:Name="fillViewport" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                               Value="{TemplateBinding Value}" Foreground="#70FFFFFF" Focusable="False"
                               IsHitTestVisible="False" BorderThickness="0">
                    <ProgressBar.Template>
                      <ControlTemplate TargetType="ProgressBar">
                        <Grid x:Name="PART_Track">
                          <Border x:Name="PART_Indicator" HorizontalAlignment="Left"
                                  Background="{TemplateBinding Foreground}"/>
                        </Grid>
                      </ControlTemplate>
                    </ProgressBar.Template>
                  </ProgressBar>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True">
                    <Setter TargetName="rail" Property="BorderBrush" Value="#FF5D728A"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    internal static GlassCalendar Calendar()=>new();

}

/// <summary>A continuous value surface that keeps Slider keyboard and automation semantics.</summary>
internal sealed class UnifiedSlider : Slider
{
    private bool _dragging;
    private FrameworkElement? _fillViewport;
    private readonly RectangleGeometry _fillClip=new() { RadiusX=13,RadiusY=13 };
    internal event EventHandler? ValueCommitted;

    public override void OnApplyTemplate()
    {
        if(_fillViewport is not null) _fillViewport.SizeChanged-=ResizeFillClip;
        base.OnApplyTemplate();
        _fillViewport=GetTemplateChild("fillViewport") as FrameworkElement;
        if(_fillViewport is null) return;
        _fillViewport.Clip=_fillClip;
        _fillViewport.SizeChanged+=ResizeFillClip;
        _fillClip.Rect=new Rect(0,0,_fillViewport.ActualWidth,_fillViewport.ActualHeight);
    }

    private void ResizeFillClip(object sender,SizeChangedEventArgs e)=>_fillClip.Rect=new Rect(new Point(),e.NewSize);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        SetFromPointer(e);
        _dragging=CaptureMouse();
        e.Handled=true;
        if(!_dragging) ValueCommitted?.Invoke(this,EventArgs.Empty);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if(!_dragging) { base.OnPreviewMouseMove(e); return; }
        SetFromPointer(e);
        e.Handled=true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(!_dragging) { base.OnPreviewMouseLeftButtonUp(e); return; }
        SetFromPointer(e);
        _dragging=false;
        ReleaseMouseCapture();
        e.Handled=true;
        ValueCommitted?.Invoke(this,EventArgs.Empty);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if(!_dragging) return;
        _dragging=false;
        ValueCommitted?.Invoke(this,EventArgs.Empty);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            ValueCommitted?.Invoke(this,EventArgs.Empty);
    }

    private void SetFromPointer(MouseEventArgs e)
    {
        double position=Math.Clamp((e.GetPosition(this).X-1)/Math.Max(1,ActualWidth-2),0,1);
        if(IsDirectionReversed) position=1-position;
        SetCurrentValue(ValueProperty,Minimum+(Maximum-Minimum)*position);
    }
}
