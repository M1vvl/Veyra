using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GlassDock.Models;

namespace GlassDock.Island;

internal sealed class IslandCompactLyrics : TextBlock
{
    private static readonly Brush Pending=Brush(Colors.White,.45);
    private Brush _sung=IslandPalette.Sung(IslandAccentColor.Default);
    private Brush _current=IslandPalette.Current(IslandAccentColor.Default);
    private LyricLine? _line;
    private string _source="";
    private int[] _characters=[];
    private double[] _starts=[];
    private readonly List<int> _pages=[];
    private readonly List<Run> _runs=[];
    private int _page=-1,_active=-2;
    private double _end=-1,_position;

    internal IslandCompactLyrics()
    {
        FontSize=12;
        FontWeight=FontWeights.SemiBold;
        TextWrapping=TextWrapping.NoWrap;
        TextAlignment=TextAlignment.Center;
        VerticalAlignment=VerticalAlignment.Center;
        HorizontalAlignment=HorizontalAlignment.Stretch;
        SizeChanged+=(_,args)=> {if(args.WidthChanged) {BuildPages();Paint(true);}};
    }

    internal void ShowTitle(string text)
    {
        if(_line is null && _source==text)return;
        _line=null;
        SetText(text);
        _starts=[];
        _position=0;
        ToolTip=null;
        Paint(true);
    }

    internal void SetAccent(IslandAccentColor color)
    {
        var current=IslandPalette.Current(color);
        if(ReferenceEquals(_current,current))return;
        _current=current;
        _sung=IslandPalette.Sung(color);
        Paint(true);
    }

    internal void ShowLine(LyricLine line,double end,double position)
    {
        if(!ReferenceEquals(_line,line) || _source!=line.Text)
        {
            _line=line;
            SetText(line.Text);
            _end=-1;
        }
        if(_end!=end)
        {
            _end=end;
            BuildTiming();
        }
        _position=position;
        Paint(false);
    }

    private void SetText(string text)
    {
        _source=text;
        _characters=StringInfo.ParseCombiningCharacters(text);
        _active=-2;
        BuildPages();
    }

    private void BuildTiming()
    {
        _starts=new double[_characters.Length];
        if(_line is null)return;
        bool timed=_line.Words is {Count:>0} words && string.Concat(words.Select(word=>word.Text))==_source;
        if(timed)
        {
            int character=0,offset=0;
            foreach(var word in _line.Words!)
            {
                int first=character;
                while(character<_characters.Length && _characters[character]<offset+word.Text.Length)character++;
                int count=character-first;
                for(int i=0;i<count;i++)
                    _starts[first+i]=word.Time.TotalSeconds+word.Duration.TotalSeconds*i/Math.Max(1,count);
                offset+=word.Text.Length;
            }
        }
        else
        {
            double duration=Math.Max(.1,_end-_line.Time.TotalSeconds);
            for(int i=0;i<_starts.Length;i++)
                _starts[i]=_line.Time.TotalSeconds+duration*i/Math.Max(1,_starts.Length);
        }
        ToolTip=timed?"逐字歌词":"逐字时间按整行时长估算";
    }

    private string Slice(int first,int end)
    {
        int start=_characters[first];
        int finish=end<_characters.Length?_characters[end]:_source.Length;
        return _source.Substring(start,finish-start);
    }

    private void BuildPages()
    {
        _pages.Clear();
        _page=-1;
        if(_characters.Length==0)return;
        double available=Math.Max(1,ActualWidth-6);
        var face=new Typeface(FontFamily,FontStyle,FontWeight,FontStretch);
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Measure(int first,int end)=>new FormattedText(Slice(first,end),CultureInfo.CurrentUICulture,
            FlowDirection,face,FontSize,_current,dpi).WidthIncludingTrailingWhitespace;
        for(int first=0;first<_characters.Length;)
        {
            _pages.Add(first);
            int low=first+1,high=_characters.Length,best=first+1;
            while(low<=high)
            {
                int middle=low+(high-low)/2;
                if(Measure(first,middle)<=available) {best=middle;low=middle+1;}
                else high=middle-1;
            }
            // Prefer a word boundary for Latin text, retaining full grapheme clusters.
            if(best<_characters.Length)
                for(int boundary=best;boundary>first+(best-first)*2/3;boundary--)
                    if(char.IsWhiteSpace(_source[_characters[boundary-1]])) {best=boundary;break;}
            first=best;
        }
    }

    private void Paint(bool force)
    {
        if(_characters.Length==0) {Inlines.Clear();_runs.Clear();return;}
        int active=-1;
        if(_line is not null)
        {
            int low=0,high=_starts.Length-1;
            while(low<=high)
            {
                int middle=low+(high-low)/2;
                if(_starts[middle]<=_position) {active=middle;low=middle+1;}
                else high=middle-1;
            }
        }
        int page=0;
        while(page+1<_pages.Count && _pages[page+1]<=Math.Max(0,active))page++;
        bool rebuild=_page!=page;
        if(!force && !rebuild && _active==active)return;
        if(rebuild)
        {
            Inlines.Clear();_runs.Clear();_page=page;
            int end=page+1<_pages.Count?_pages[page+1]:_characters.Length;
            for(int i=_pages[page];i<end;i++)
            {
                var run=new Run(Slice(i,i+1));
                _runs.Add(run);Inlines.Add(run);
            }
        }
        for(int i=0;i<_runs.Count;i++)
        {
            int index=_pages[page]+i;
            _runs[i].Foreground=_line is null?_current:index<active?_sung:index==active?_current:Pending;
            _runs[i].FontWeight=index==active?FontWeights.Bold:FontWeights.SemiBold;
        }
        _active=active;
    }

    private static Brush Brush(Color color,double opacity)
    {
        var brush=new SolidColorBrush(color){Opacity=opacity};brush.Freeze();return brush;
    }
}
