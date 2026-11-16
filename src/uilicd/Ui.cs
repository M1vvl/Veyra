using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock;

internal static class Ui
{
    private static readonly Style KeyboardButtonFocus=CreateKeyboardButtonFocus();
    private static Style CreateKeyboardButtonFocus()
    {
        var style=new Style(typeof(Control));
        var template=new ControlTemplate(typeof(Control)); var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(12));
        border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        border.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(190,235,242,250)));
        border.SetValue(FrameworkElement.MarginProperty,new Thickness(1));
        border.SetValue(UIElement.IsHitTestVisibleProperty,false);
        template.VisualTree=border; style.Setters.Add(new Setter(Control.TemplateProperty,template));
        return style;
    }
    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(35, 43, 55));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(100, 111, 128));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(35, 115, 231));
    public static Button Button(object content, string label, Action action, double width = 44, double height = 44, byte hoverAlpha = 50)
    {
        var b = new Button { Content=content, Width=width, Height=height, Background=Brushes.Transparent, BorderThickness=new Thickness(0), Foreground=Ink, Cursor=System.Windows.Input.Cursors.Hand, ToolTip=label, FocusVisualStyle=KeyboardButtonFocus, Padding=new Thickness(0) };
        AutomationProperties.SetName(b, label);
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.Name="surface";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp); template.VisualTree=border;
        var hover = new Trigger { Property=UIElement.IsMouseOverProperty, Value=true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(hoverAlpha,255,255,255)), "surface"));
        template.Triggers.Add(hover);
        var press = new Trigger { Property=System.Windows.Controls.Button.IsPressedProperty, Value=true };
        press.Setters.Add(new Setter(UIElement.OpacityProperty, .65, "surface")); template.Triggers.Add(press);
        // WPF shows FocusVisualStyle for keyboard navigation. A persistent focus
        // property trigger also outlines mouse clicks and survives pointer leave.
        b.Template=template;
        b.Click += (_,_) => { try { action(); } catch(Exception ex) { Program.Notify(ex.Message); } };
        return b;
    }
    public static TextBlock Glyph(string value, double size=23) => new() { Text=value, FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize=size, Foreground=Ink, VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center };
    public static FrameworkElement WindowsStart(double size)
    {
        // Windows 11 Start: four equal, axis-aligned blue panes with a 1px gap at 23px.
        // Vector geometry keeps the official four-pane silhouette sharp at every DPI.
        var drawing=new DrawingGroup();
        var blue=new LinearGradientBrush { StartPoint=new Point(0,0),EndPoint=new Point(1,1),MappingMode=BrushMappingMode.Absolute };
        blue.StartPoint=new Point(0,0); blue.EndPoint=new Point(23,23);
        blue.GradientStops.Add(new GradientStop(Color.FromRgb(88,215,251),0));
        blue.GradientStops.Add(new GradientStop(Color.FromRgb(22,164,228),.48));
        blue.GradientStops.Add(new GradientStop(Color.FromRgb(0,112,197),1));
        foreach(var rect in new[]{new Rect(0,0,11,11),new Rect(12,0,11,11),new Rect(0,12,11,11),new Rect(12,12,11,11)})
            drawing.Children.Add(new GeometryDrawing(blue,null,new RectangleGeometry(rect)));
        drawing.Freeze();
        return new Image { Source=new DrawingImage(drawing),Width=size,Height=size,Stretch=Stretch.Uniform };
    }
    public static TextBlock Text(string value, double size=13, Brush? color=null) => new() { Text=value, FontSize=size, Foreground=color??Ink, TextWrapping=TextWrapping.Wrap, FontFamily=new FontFamily("Segoe UI Variable, Microsoft YaHei UI") };
    public static MenuItem Menu(string text, Action action, bool check=false, bool isChecked=false)
    {
        var m=new MenuItem { Header=text, IsCheckable=check, IsChecked=isChecked };
        m.Click+=(_,_)=> { try { action(); } catch(Exception ex) { Program.Notify(ex.Message); } }; return m;
    }
}
