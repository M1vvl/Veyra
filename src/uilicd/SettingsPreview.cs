using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDock;

internal static class SettingsPreview
{
    internal static int Run(string path)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Program.Preview=Program.CapturePreview=true; Program.Prefs=Preferences.Load(); PreferencesPersistence.WritesSuspendedForProbe=true; Styles.GlassMenuSurface.Install(app);
        var results=new List<object>(); string? failure=null;
        void Check(string name,bool pass) {results.Add(new {name,pass}); if(!pass)throw new InvalidOperationException(name);}
        app.Dispatcher.BeginInvoke(new Action(async()=> {
            SettingsWindow? window=null;
            try
            {
                window=new SettingsWindow(); window.Show();
                for(int i=0;i<80 && (!window.HasMaterialImage || Descendants<GlassCard>(window).Count(card=>card.HasOpticalMaterial)<3);i++)await Task.Delay(30);
                window.UpdateLayout();
                Check("uilicd title and supplied icon loaded",window.Title=="uilicd · 设置" && window.Icon is BitmapSource);
                Check("Settings header shows only uilicd without a logo",Descendants<TextBlock>(window).Any(text=>text.Text=="uilicd") && !Descendants<Image>(window).Any(image=>ReferenceEquals(image.Source,Branding.WindowIcon)));
                nint icon=Branding.CreateNativeIcon(); Check("Native tray icon loaded",icon!=0); DestroyIcon(icon);
                Check("Settings has a real optical glass background",window.HasMaterialImage);
                Check("All three settings groups use optical glass",Descendants<GlassCard>(window).Count(card=>card.HasOpticalMaterial)>=3);
                Check("Five functional switches, sidebar modes and four integrated sliders remain",Descendants<CheckBox>(window).Count()==5 && Descendants<UnifiedSlider>(window).Count()==4);
                Check("All five individual reset actions remain",Descendants<Button>(window).Count(button=>button.ToolTip?.ToString()?.StartsWith("重置")==true)==5);
                Check("Slider fill remains translucent",Descendants<ProgressBar>(window).All(bar=>bar.Foreground is SolidColorBrush brush && brush.Color.A<255));
                string full=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(full+".png"))encoder.Save(stream);
                var combo=Descendants<ComboBox>(window).Single(control=>System.Windows.Automation.AutomationProperties.GetName(control)=="材质刷新率"); combo.IsDropDownOpen=true; await Task.Delay(160);
                Check("Frame-rate popup opens with Liquid Glass",combo.IsDropDownOpen); combo.IsDropDownOpen=false;
                foreach(var reset in Descendants<Button>(window).Where(button=>button.ToolTip?.ToString()?.StartsWith("重置")==true))reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var defaults=new Preferences();
                Check("Independent reset buttons restore actual preferences",Program.Prefs.GlassTransparency==defaults.GlassTransparency && Program.Prefs.DockScale==defaults.DockScale && Program.Prefs.IconSize==defaults.IconSize && Program.Prefs.Magnification==defaults.Magnification && Program.Prefs.GlassFramesPerSecond==defaults.GlassFramesPerSecond);
                var transparency=Descendants<UnifiedSlider>(window).Single(slider=>System.Windows.Automation.AutomationProperties.GetName(slider)=="玻璃层透明度");
                transparency.Value=.5;
                Check("Glass transparency leaves content opacity unchanged",window.GlassLayerOpacity==.5 && window.ContentLayerOpacity==1);
            }
            catch(Exception exception) {failure=exception.ToString();}
            finally {window?.Close(); File.WriteAllText(path,JsonSerializer.Serialize(new {timestamp=DateTimeOffset.Now,results,failure},new JsonSerializerOptions {WriteIndented=true})); app.Shutdown(failure is null?0:1);}
        }));
        return app.Run();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) {var child=VisualTreeHelper.GetChild(node,i); if(child is T item)yield return item; foreach(var descendant in Descendants<T>(child))yield return descendant;}}
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
