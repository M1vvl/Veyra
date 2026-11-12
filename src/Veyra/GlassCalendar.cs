using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock;

/// <summary>Local date navigation drawn over the control center's shared glass surface.</summary>
internal sealed class GlassCalendar : Grid
{
    private static readonly Brush SelectedFill=new LinearGradientBrush(
        Color.FromArgb(75,255,255,255),Color.FromArgb(18,255,255,255),90);
    private readonly TextBlock _month=Ui.Text("",16);
    private readonly List<TextBlock> _chrome=[];
    private readonly List<DayCell> _cells=[];
    private readonly Button _previous,_next;
    private Brush _ink=Ui.Ink;
    private DateTime _focusDate=DateTime.Today;
    internal DateTime SelectedDate { get; private set; }=DateTime.Today;
    internal DateTime DisplayMonth { get; private set; }=new(DateTime.Today.Year,DateTime.Today.Month,1);

    internal GlassCalendar()
    {
        Focusable=true; FocusVisualStyle=null;
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Once);
        AutomationProperties.SetName(this,"日历");
        ToolTip="方向键选择日期，PageUp / PageDown 切换月份，Home 到月初";
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(32) });
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(24) });
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(198) });

        var header=new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        foreach(double width in new[]{30d,30d,44d}) header.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(width) });
        _month.FontWeight=FontWeights.SemiBold; _month.VerticalAlignment=VerticalAlignment.Center; _chrome.Add(_month); header.Children.Add(_month);
        _previous=NavigationButton(Ui.Glyph("\uE76B",13),"上个月",()=>MoveMonth(-1));
        _next=NavigationButton(Ui.Glyph("\uE76C",13),"下个月",()=>MoveMonth(1));
        var today=NavigationButton(Ui.Text("今天",12),"回到今天",()=>SelectDate(DateTime.Today));
        Grid.SetColumn(_previous,1); Grid.SetColumn(_next,2); Grid.SetColumn(today,3);
        header.Children.Add(_previous); header.Children.Add(_next); header.Children.Add(today); Children.Add(header);

        var weekdays=new UniformGrid { Rows=1,Columns=7 };
        foreach(string name in new[]{"一","二","三","四","五","六","日"})
        {
            var label=Ui.Text(name,11); label.Opacity=.6; label.VerticalAlignment=VerticalAlignment.Center;
            label.HorizontalAlignment=HorizontalAlignment.Center; _chrome.Add(label); weekdays.Children.Add(label);
        }
        Grid.SetRow(weekdays,1); Children.Add(weekdays);

        var days=new UniformGrid { Rows=6,Columns=7 };
        for(int i=0;i<42;i++)
        {
            int index=i;
            var label=Ui.Text("",12); label.HorizontalAlignment=HorizontalAlignment.Center; label.VerticalAlignment=VerticalAlignment.Center;
            var selected=new Border { CornerRadius=new CornerRadius(15),Background=SelectedFill,BorderThickness=new Thickness(.7),Visibility=Visibility.Hidden };
            var todayRing=new Border { CornerRadius=new CornerRadius(15),BorderThickness=new Thickness(1),Visibility=Visibility.Hidden };
            var face=new Grid { Width=30,Height=30 }; face.Children.Add(selected); face.Children.Add(todayRing); face.Children.Add(label);
            var button=Ui.Button(face,"",()=> { if(_cells[index].Date is DateTime date) SelectDate(date); },double.NaN,32);
            button.Focusable=false; button.Margin=new Thickness(1,0,1,0);
            _cells.Add(new DayCell(button,label,selected,todayRing)); days.Children.Add(button);
        }
        Grid.SetRow(days,2); Children.Add(days); RefreshMonth();
    }

    private Button NavigationButton(TextBlock label,string name,Action action)
    {
        _chrome.Add(label);
        return Ui.Button(label,name,action,double.NaN,28);
    }

    internal void ApplyInk(Brush ink)
    {
        _ink=ink;
        foreach(var label in _chrome) label.Foreground=ink;
        var selectionEdge=ink.CloneCurrentValue(); selectionEdge.Opacity=.22; selectionEdge.Freeze();
        var todayEdge=ink.CloneCurrentValue(); todayEdge.Opacity=.55; todayEdge.Freeze();
        foreach(var cell in _cells)
        {
            cell.Label.Foreground=ink;
            cell.Selected.BorderBrush=selectionEdge;
            cell.Today.BorderBrush=todayEdge;
        }
    }

    private void MoveMonth(int delta)
    {
        DisplayMonth=MonthAtOffset(DisplayMonth,delta);
        _focusDate=new DateTime(DisplayMonth.Year,DisplayMonth.Month,Math.Min(_focusDate.Day,DateTime.DaysInMonth(DisplayMonth.Year,DisplayMonth.Month)));
        RefreshMonth(); Focus();
    }

    private void SelectDate(DateTime date)
    {
        SelectedDate=date.Date; _focusDate=SelectedDate;
        DisplayMonth=new DateTime(date.Year,date.Month,1);
        RefreshMonth(); Focus();
    }

    private void RefreshMonth()
    {
        _month.Text=DisplayMonth.ToString("yyyy年M月",CultureInfo.GetCultureInfo("zh-CN"));
        _previous.IsEnabled=DisplayMonth>DateTime.MinValue.Date;
        _next.IsEnabled=DisplayMonth.Year<9999 || DisplayMonth.Month<12;
        int offset=((int)DisplayMonth.DayOfWeek+6)%7;
        int firstDay=DateOnly.FromDateTime(DisplayMonth).DayNumber-offset;
        for(int i=0;i<_cells.Count;i++)
        {
            var cell=_cells[i]; int dayNumber=firstDay+i;
            if(dayNumber<0 || dayNumber>DateOnly.MaxValue.DayNumber)
            {
                cell.Date=null; cell.Button.Visibility=Visibility.Hidden; continue;
            }
            var date=DateOnly.FromDayNumber(dayNumber).ToDateTime(TimeOnly.MinValue);
            cell.Date=date; cell.Button.Visibility=Visibility.Visible;
            cell.Button.Opacity=date.Month==DisplayMonth.Month?1:.36;
            cell.Label.Text=date.Day.ToString(CultureInfo.InvariantCulture);
            bool selected=date==SelectedDate,today=date==DateTime.Today;
            cell.Label.FontWeight=selected || today?FontWeights.SemiBold:FontWeights.Normal;
            cell.Selected.Visibility=selected?Visibility.Visible:Visibility.Hidden;
            cell.Today.Visibility=today?Visibility.Visible:Visibility.Hidden;
            string description=date.ToString("yyyy年M月d日 dddd",CultureInfo.GetCultureInfo("zh-CN"));
            AutomationProperties.SetName(cell.Button,description+(today?"，今天":"")+(selected?"，已选择":""));
            cell.Button.ToolTip=description;
        }
        ApplyInk(_ink);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        DateTime date;
        switch(e.Key)
        {
            case Key.Left: date=DayAtOffset(_focusDate,-1); break;
            case Key.Right: date=DayAtOffset(_focusDate,1); break;
            case Key.Up: date=DayAtOffset(_focusDate,-7); break;
            case Key.Down: date=DayAtOffset(_focusDate,7); break;
            case Key.PageUp: date=DateInMonth(_focusDate,-1); break;
            case Key.PageDown: date=DateInMonth(_focusDate,1); break;
            case Key.Home: date=(Keyboard.Modifiers&ModifierKeys.Control)!=0?DateTime.Today:new DateTime(_focusDate.Year,_focusDate.Month,1); break;
            case Key.End: date=new DateTime(_focusDate.Year,_focusDate.Month,DateTime.DaysInMonth(_focusDate.Year,_focusDate.Month)); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        SelectDate(date); e.Handled=true;
    }

    private static DateTime DayAtOffset(DateTime date,int offset)
    {
        int day=Math.Clamp(DateOnly.FromDateTime(date).DayNumber+offset,0,DateOnly.MaxValue.DayNumber);
        return DateOnly.FromDayNumber(day).ToDateTime(TimeOnly.MinValue);
    }

    private static DateTime MonthAtOffset(DateTime date,int offset)
    {
        int month=Math.Clamp((date.Year-1)*12+date.Month-1+offset,0,9999*12-1);
        return new DateTime(month/12+1,month%12+1,1);
    }

    private static DateTime DateInMonth(DateTime date,int offset)
    {
        var month=MonthAtOffset(date,offset);
        return new DateTime(month.Year,month.Month,Math.Min(date.Day,DateTime.DaysInMonth(month.Year,month.Month)));
    }

    private sealed record DayCell(Button Button,TextBlock Label,Border Selected,Border Today)
    {
        internal DateTime? Date;
    }
}
