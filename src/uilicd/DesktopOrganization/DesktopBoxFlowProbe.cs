using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal static class DesktopBoxFlowProbe
{
    internal static int Run(string path)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Program.Preview=Program.CapturePreview=true; Program.Prefs=new Preferences();
        PreferencesPersistence.WritesSuspendedForProbe=true; Styles.GlassMenuSurface.Install(app);
        var results=new List<object>(); string? failure=null; DesktopBoxManager? manager=null;
        void Check(string name,bool pass) {results.Add(new {name,pass}); if(!pass)throw new InvalidOperationException(name);}
        string configuration=Path.Combine(Path.GetTempPath(),"uilicd-flow-"+Guid.NewGuid().ToString("N")+".json");
        var boxes=new[] {
            new DesktopBoxDefinition {Id="upper",Name="Flow upper",Left=300,Top=160,Width=220,Height=244,AutoSize=false},
            new DesktopBoxDefinition {Id="lower",Name="Flow lower",Left=300,Top=430,Width=220,Height=178,AutoSize=false},
            new DesktopBoxDefinition {Id="other",Name="Flow other column",Left=560,Top=160,Width=220,Height=244,AutoSize=false} };
        File.WriteAllText(configuration,JsonSerializer.Serialize(new DesktopBoxConfiguration {PresetVersion=99,GroupEdge="Free",GroupLeft=250,GroupTop=80,Boxes=boxes.ToList()}));
        app.Dispatcher.BeginInvoke(new Action(async()=> {
            try
            {
                manager=new DesktopBoxManager(configuration); manager.Start(); await Task.Delay(200);
                var windows=manager.DesktopWindows.ToDictionary(window=>window.BoxId);
                var original=windows.ToDictionary(pair=>pair.Key,pair=>pair.Value.VisibleBoundsPixels);
                var button=Descendants<Button>(windows["upper"]).Single(item=>item.ToolTip?.ToString()=="展开 / 收起");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
                var collapsed=windows["upper"].VisibleBoundsPixels;
                double freed=original["upper"].Height-collapsed.Height;
                Check("Real collapse button shrinks the selected box",freed>100 && manager.Configuration.Boxes.Single(box=>box.Id=="upper").Collapsed);
                Check("Lower box follows the exact released height",Math.Abs(windows["lower"].VisibleBoundsPixels.Top-(original["lower"].Top-freed))<2);
                Check("Unrelated column stays in place",(windows["other"].VisibleBoundsPixels.TopLeft-original["other"].TopLeft).Length<2);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
                Check("Expanding restores the original column gap",(windows["lower"].VisibleBoundsPixels.TopLeft-original["lower"].TopLeft).Length<2);
                manager.SetCollapsed(manager.Configuration.Boxes,true); await Task.Delay(60);
                Check("Collapse-all also compacts lower boxes",windows["lower"].VisibleBoundsPixels.Top<original["lower"].Top-100);
                manager.SetCollapsed(manager.Configuration.Boxes,false); await Task.Delay(60);
                Check("Expand-all restores column positions without drift",windows.All(pair=>(pair.Value.VisibleBoundsPixels.TopLeft-original[pair.Key].TopLeft).Length<2));
            }
            catch(Exception exception){failure=exception.ToString();}
            finally
            {
                manager?.Dispose(); foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path,JsonSerializer.Serialize(new {scope="owned mapping boxes only; isolated configuration",results,failure},new JsonSerializerOptions {WriteIndented=true}));
                app.Shutdown(failure is null?0:1);
            }
        }));
        return app.Run();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) {var child=VisualTreeHelper.GetChild(node,i); if(child is T found)yield return found; foreach(var descendant in Descendants<T>(child))yield return descendant;}}
}
