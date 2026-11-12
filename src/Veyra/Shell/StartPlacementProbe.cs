using System.Windows;
using System.Windows.Controls;

namespace GlassDock.Shell;

/// <summary>Opt-in, targetable development window that invokes the actual Dock Start action.</summary>
internal static class StartPlacementProbe
{
    internal static void Show()
    {
        var button=new Button { Content="打开 Dock 开始菜单",Margin=new Thickness(24),Padding=new Thickness(16) };
        var window=new Window { Title="GlassDock Start placement probe",Content=button,Width=360,Height=150,
            WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.NoResize };
        button.Click+=(_,_)=>Program.Dock?.OpenStart();
        window.Show();
    }
}
