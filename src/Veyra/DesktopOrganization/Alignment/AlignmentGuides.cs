using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization.Alignment;

/// <summary>WitchDrawer-style alignment lines, allocated only for a live drag.</summary>
internal sealed class AlignmentGuides : IDisposable
{
    private GuideWindow? _vertical,_horizontal;
    internal void Show(SnapAlignment snap)
    {
        if(snap.VerticalGuideX is double x)
            (_vertical??=new GuideWindow()).Line(new Point(x,snap.VerticalGuideYMin),new Point(x,snap.VerticalGuideYMax));
        else _vertical?.Hide();
        if(snap.HorizontalGuideY is double y)
            (_horizontal??=new GuideWindow()).Line(new Point(snap.HorizontalGuideXMin,y),new Point(snap.HorizontalGuideXMax,y));
        else _horizontal?.Hide();
    }
    internal void Hide() { _vertical?.Hide(); _horizontal?.Hide(); }
    public void Dispose() { _vertical?.Close(); _horizontal?.Close(); _vertical=_horizontal=null; }

    private sealed class GuideWindow : Window
    {
        private readonly System.Windows.Shapes.Line _line=new() { Stroke=new SolidColorBrush(Color.FromArgb(215,213,239,255)),StrokeThickness=1.3,StrokeDashArray=new DoubleCollection { 4,3 } };
        internal GuideWindow()
        {
            WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=Brushes.Transparent;
            ShowInTaskbar=false; ShowActivated=false; Topmost=true; IsHitTestVisible=false; Focusable=false; ResizeMode=ResizeMode.NoResize;
            var canvas=new Canvas(); canvas.Children.Add(_line); Content=canvas;
            SourceInitialized+=(_,_)=> {
                DesktopNative.Configure(this);
                var handle=new WindowInteropHelper(this).Handle;
                SetWindowLong(handle,-20,GetWindowLong(handle,-20)|0x20);
                HwndSource.FromHwnd(handle)?.AddHook((nint h,int message,nint w,nint l,ref bool handled)=> {
                    if(message==0x84) { handled=true; return -1; } return 0;
                });
            };
        }
        internal void Line(Point a,Point b)
        {
            if(!IsVisible) Show();
            var dpi=VisualTreeHelper.GetDpi(this);
            double left=Math.Min(a.X,b.X)-2,top=Math.Min(a.Y,b.Y)-2;
            double width=Math.Max(4,Math.Abs(a.X-b.X)+4),height=Math.Max(4,Math.Abs(a.Y-b.Y)+4);
            SetWindowPos(new WindowInteropHelper(this).Handle,-1,(int)left,(int)top,(int)Math.Ceiling(width),(int)Math.Ceiling(height),0x10|0x40);
            _line.X1=(a.X-left)/dpi.DpiScaleX; _line.X2=(b.X-left)/dpi.DpiScaleX;
            _line.Y1=(a.Y-top)/dpi.DpiScaleY; _line.Y2=(b.Y-top)/dpi.DpiScaleY;
        }
        [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(nint window,int index,int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    }
}
