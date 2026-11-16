using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed class BoxNameWindow : GlassWindow
{
    private readonly TextBox _name;
    protected override bool NoActivate=>false;
    internal BoxNameWindow(string title,string initial,Action<string> commit):base("uilicd · "+title)
    {
        ShowActivated=true; Width=340; Height=174; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var layout=new StackPanel { Margin=new Thickness(20,17,20,17) };
        var heading=Ui.Text(title,18); heading.FontWeight=FontWeights.SemiBold; layout.Children.Add(heading);
        _name=new TextBox { Text=initial,MaxLength=40,FontSize=15,Padding=new Thickness(10,8,10,8),
            Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=Brushes.White };
        layout.Children.Add(new GlassCard { CornerRadius=12,Margin=new Thickness(0,12,0,8),Content=_name });
        var actions=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        void Save()
        {
            string value=_name.Text.Trim(); if(value.Length==0) { _name.Focus(); return; }
            try { commit(value); Close(); }
            catch(InvalidOperationException exception) { _name.ToolTip=exception.Message; _name.Focus(); }
        }
        actions.Children.Add(Ui.Button(Ui.Text("取消",12),"取消",Close,56,30));
        actions.Children.Add(Ui.Button(Ui.Text("确定",12),"确定",Save,56,30)); layout.Children.Add(actions);
        ContentLayer.Children.Add(layout);
        Loaded+=(_,_)=> { Reposition(); _name.Focus(); _name.SelectAll(); };
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Enter) { Save(); e.Handled=true; } else if(e.Key==Key.Escape) { Close(); e.Handled=true; } };
    }
    protected override void Reposition() { if(IsLoaded) { SetSurface(new Rect(8,8,Width-16,Height-16),22); RenderGlass(); } }
    protected override void OnMaterialLuminance(double luminance)
    {
        var ink=luminance<.48?Brushes.White:Ui.Ink;
        _name.Foreground=ink;
        void Apply(DependencyObject node) { if(node is TextBlock text)text.Foreground=ink; for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)Apply(VisualTreeHelper.GetChild(node,i)); }
        Apply(ContentLayer);
    }
}
