using System.Windows;

namespace GlassDock.DesktopOrganization;

/// <summary>Shared visible-edge spacing tokens and bounded nearest-slot placement.</summary>
internal static class DesktopBoxSpacing
{
    internal const double Unit=8,Gap=Unit,ControlGap=Unit/2;
    internal static bool Separated(Rect first,Rect second,Size gap)=>
        first.Right+gap.Width<=second.Left+.05 || first.Left>=second.Right+gap.Width-.05
        || first.Bottom+gap.Height<=second.Top+.05 || first.Top>=second.Bottom+gap.Height-.05;

    internal static Rect ChildArea(Rect screen,Rect control,Size extent,Size gap,string edge)
    {
        var below=new Rect(screen.Left,Math.Clamp(control.Bottom+gap.Height,screen.Top,screen.Bottom),screen.Width,
            Math.Max(0,screen.Bottom-Math.Max(screen.Top,control.Bottom+gap.Height)));
        var above=new Rect(screen.Left,screen.Top,screen.Width,Math.Max(0,Math.Min(screen.Bottom,control.Top-gap.Height)-screen.Top));
        var right=new Rect(Math.Clamp(control.Right+gap.Width,screen.Left,screen.Right),screen.Top,
            Math.Max(0,screen.Right-Math.Max(screen.Left,control.Right+gap.Width)),screen.Height);
        var left=new Rect(screen.Left,screen.Top,Math.Max(0,Math.Min(screen.Right,control.Left-gap.Width)-screen.Left),screen.Height);
        Span<Rect> choices=stackalloc Rect[4];
        if(edge=="Left") { choices[0]=right; choices[1]=below; choices[2]=above; choices[3]=left; }
        else if(edge=="Right") { choices[0]=left; choices[1]=below; choices[2]=above; choices[3]=right; }
        else { choices[0]=below; choices[1]=above; choices[2]=right; choices[3]=left; }
        foreach(var area in choices)if(area.Width>=extent.Width-.1 && area.Height>=extent.Height-.1)return area;
        return screen;
    }

    internal static bool TryPlace(Rect desired,ReadOnlySpan<Rect> obstacles,Rect area,Size gap,out Rect placement)
    {
        placement=desired;
        if(desired.Width>area.Width+.1 || desired.Height>area.Height+.1)return false;
        double maxX=area.Right-desired.Width,maxY=area.Bottom-desired.Height;
        var clamped=new Rect(Math.Clamp(desired.Left,area.Left,maxX),Math.Clamp(desired.Top,area.Top,maxY),desired.Width,desired.Height);
        if(Clear(clamped,obstacles,gap)) { placement=clamped; return true; }
        Span<double> xs=stackalloc double[obstacles.Length*2+3],ys=stackalloc double[obstacles.Length*2+3];
        int nx=0,ny=0;
        Add(xs,ref nx,clamped.Left,area.Left,maxX); Add(xs,ref nx,area.Left,area.Left,maxX); Add(xs,ref nx,maxX,area.Left,maxX);
        Add(ys,ref ny,clamped.Top,area.Top,maxY); Add(ys,ref ny,area.Top,area.Top,maxY); Add(ys,ref ny,maxY,area.Top,maxY);
        foreach(var obstacle in obstacles)
        {
            Add(xs,ref nx,obstacle.Left-desired.Width-gap.Width,area.Left,maxX); Add(xs,ref nx,obstacle.Right+gap.Width,area.Left,maxX);
            Add(ys,ref ny,obstacle.Top-desired.Height-gap.Height,area.Top,maxY); Add(ys,ref ny,obstacle.Bottom+gap.Height,area.Top,maxY);
        }
        Sort(xs[..nx],desired.Left); Sort(ys[..ny],desired.Top);
        double best=double.PositiveInfinity;
        for(int ix=0;ix<Math.Min(nx,24);ix++)for(int iy=0;iy<Math.Min(ny,24);iy++)
        {
            double dx=xs[ix]-desired.Left,dy=ys[iy]-desired.Top,score=dx*dx+dy*dy;
            if(score>=best)continue;
            var candidate=new Rect(xs[ix],ys[iy],desired.Width,desired.Height);
            if(!Clear(candidate,obstacles,gap))continue;
            placement=candidate; best=score;
        }
        return double.IsFinite(best);
    }
    private static bool Clear(Rect candidate,ReadOnlySpan<Rect> obstacles,Size gap)
    { foreach(var obstacle in obstacles)if(!Separated(candidate,obstacle,gap))return false; return true; }
    private static void Add(Span<double> values,ref int count,double value,double min,double max)
    {
        value=Math.Clamp(value,min,max);
        for(int index=0;index<count;index++)if(Math.Abs(values[index]-value)<.05)return;
        values[count++]=value;
    }
    private static void Sort(Span<double> values,double origin)
    {
        for(int index=1;index<values.Length;index++)
        {
            double value=values[index]; int before=index-1;
            while(before>=0 && Math.Abs(values[before]-origin)>Math.Abs(value-origin)) { values[before+1]=values[before]; before--; }
            values[before+1]=value;
        }
    }
}
