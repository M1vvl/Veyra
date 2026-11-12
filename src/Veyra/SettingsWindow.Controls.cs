using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock;

internal sealed partial class SettingsWindow
{
    private StackPanel Section(string title)
    {
        var header=Ui.Text(title,11); header.FontWeight=FontWeights.SemiBold; header.Opacity=.9; header.Margin=new Thickness(8,10,0,7); _sections.Children.Add(header);
        var body=new StackPanel {Margin=new Thickness(13,7,13,7)};
        _sections.Children.Add(new GlassCard {CornerRadius=18,Tint=.02f,Content=body,Margin=new Thickness(0,0,0,4)}); return body;
    }
    private static void AddSwitch(Panel panel,string title,bool initial,Func<bool,bool> change,string? error=null)
    {
        var row=new Grid {MinHeight=35}; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var label=Ui.Text(title,13); label.VerticalAlignment=VerticalAlignment.Center; row.Children.Add(label);
        var value=new CheckBox {IsChecked=initial,Width=39,Height=23,VerticalAlignment=VerticalAlignment.Center,ToolTip=error??title,Cursor=Cursors.Hand};
        System.Windows.Automation.AutomationProperties.SetName(value,title);
        var template=new ControlTemplate(typeof(CheckBox));
        var track=new FrameworkElementFactory(typeof(Border)); track.Name="track"; track.SetValue(Border.CornerRadiusProperty,new CornerRadius(11.5)); track.SetValue(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(24,130,145,160))); track.SetValue(Border.BorderThicknessProperty,new Thickness(.7)); track.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(85,235,245,255)));
        var thumb=new FrameworkElementFactory(typeof(Border)); thumb.Name="knob"; thumb.SetValue(FrameworkElement.WidthProperty,17d); thumb.SetValue(FrameworkElement.HeightProperty,17d); thumb.SetValue(FrameworkElement.MarginProperty,new Thickness(2)); thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left); thumb.SetValue(Border.CornerRadiusProperty,new CornerRadius(8.5)); thumb.SetValue(Border.BackgroundProperty,new LinearGradientBrush(Color.FromArgb(230,255,255,255),Color.FromArgb(160,220,232,244),90)); track.AppendChild(thumb); template.VisualTree=track;
        var selected=new Trigger {Property=ToggleButton.IsCheckedProperty,Value=true}; selected.Setters.Add(new Setter(Border.BackgroundProperty,new LinearGradientBrush(Color.FromArgb(150,75,160,220),Color.FromArgb(105,50,125,210),90),"track")); selected.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right,"knob")); template.Triggers.Add(selected); value.Template=template;
        value.Click+=(_,_)=>value.IsChecked=change(value.IsChecked==true);
        label.MouseLeftButtonUp+=(_,e)=> {value.IsChecked=!value.IsChecked; value.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled=true;};
        Grid.SetColumn(value,1); row.Children.Add(value); panel.Children.Add(row);
    }
    private static void AddSlider(Panel panel,string title,double min,double max,double initial,double defaultValue,Action<double> change,Func<double,string> format)
    {
        var header=new Grid {Margin=new Thickness(0,7,0,2)}; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto}); header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var caption=Ui.Text(title,12); caption.VerticalAlignment=VerticalAlignment.Center; header.Children.Add(caption);
        var number=Ui.Text(format(initial),11); number.Opacity=.7; number.Margin=new Thickness(6,0,8,0); number.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(number,1); header.Children.Add(number);
        var slider=new UnifiedSlider {Minimum=min,Maximum=max,Value=initial,SmallChange=(max-min)/100,LargeChange=(max-min)/10,Style=ControlStyles.Slider,Margin=new Thickness(0,2,0,6),Height=23};
        System.Windows.Automation.AutomationProperties.SetName(slider,title);
        var reset=Ui.Button(Ui.Glyph("\uE777",11),"重置"+title,()=> {if(slider.Value==defaultValue) {change(defaultValue); Program.ApplyPreferences();} else slider.Value=defaultValue;},25,23,30); Grid.SetColumn(reset,2); header.Children.Add(reset);
        panel.Children.Add(header); panel.Children.Add(slider);
        slider.ValueChanged+=(_,_)=> {number.Text=format(slider.Value); change(slider.Value); Program.ApplyPreferences();};
    }
    private static void AddFrameRate(Panel panel,int defaultValue)
    {
        var rates=new[]{6,15,30,60};
        var row=new Grid {Margin=new Thickness(0,4,0,3)}; row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(82)}); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var label=Ui.Text("材质刷新率",12); label.VerticalAlignment=VerticalAlignment.Center; row.Children.Add(label);
        var profiles=new ComboBox {Height=29,ItemsSource=rates.Select(rate=>$"{rate} fps").ToArray(),SelectedIndex=Array.FindIndex(rates,rate=>Program.Prefs.GlassFramesPerSecond<=rate),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
        profiles.Template=ProfileTemplate(); Grid.SetColumn(profiles,1); row.Children.Add(profiles);
        var reset=Ui.Button(Ui.Glyph("\uE777",11),"重置材质刷新率",()=>profiles.SelectedIndex=Array.IndexOf(rates,defaultValue),25,23,30); reset.Margin=new Thickness(7,0,0,0); Grid.SetColumn(reset,2); row.Children.Add(reset);
        profiles.SelectionChanged+=(_,_)=> {if(profiles.SelectedIndex<0)return; Program.Prefs.GlassFramesPerSecond=rates[profiles.SelectedIndex]; Program.ApplyPreferences();}; panel.Children.Add(row);
    }
    private static ControlTemplate ProfileTemplate()
    {
        var template=new ControlTemplate(typeof(ComboBox)); var grid=new FrameworkElementFactory(typeof(Grid));
        var toggle=new FrameworkElementFactory(typeof(ToggleButton)); toggle.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsDropDownOpen") {RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent),Mode=BindingMode.TwoWay});
        var face=new ControlTemplate(typeof(ToggleButton)); var border=new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty,new CornerRadius(9)); border.SetValue(Border.BorderThicknessProperty,new Thickness(.7)); border.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(65,225,240,255))); border.SetValue(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(12,240,248,255))); var arrow=new FrameworkElementFactory(typeof(TextBlock)); arrow.SetValue(TextBlock.TextProperty,"⌄"); arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right); arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center); arrow.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,10,2)); border.AppendChild(arrow); face.VisualTree=border; toggle.SetValue(Control.TemplateProperty,face); grid.AppendChild(toggle);
        var selection=new FrameworkElementFactory(typeof(ContentPresenter)); selection.SetValue(UIElement.IsHitTestVisibleProperty,false); selection.SetValue(FrameworkElement.MarginProperty,new Thickness(10,0,28,0)); selection.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center); selection.SetBinding(ContentPresenter.ContentProperty,new Binding("SelectionBoxItem") {RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)}); grid.AppendChild(selection);
        var popup=new FrameworkElementFactory(typeof(Popup)); popup.Name="PART_Popup"; popup.SetValue(Popup.PlacementProperty,PlacementMode.Bottom); popup.SetValue(Popup.AllowsTransparencyProperty,true); popup.SetValue(Popup.PopupAnimationProperty,PopupAnimation.None); popup.SetBinding(Popup.IsOpenProperty,new Binding("IsDropDownOpen") {RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent),Mode=BindingMode.TwoWay});
        var glass=new FrameworkElementFactory(typeof(Styles.GlassMenuSurface)); var scroll=new FrameworkElementFactory(typeof(ScrollViewer)); scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Hidden); scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); glass.AppendChild(scroll); popup.AppendChild(glass); grid.AppendChild(popup); template.VisualTree=grid; return template;
    }
}
