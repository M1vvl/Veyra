namespace GlassDock.Docking;

internal static class DockLayout
{
    internal static double Influence(double distance,double radius=138)=>Math.Pow(Math.Cos(Math.Min(1,Math.Max(0,distance)/radius)*Math.PI*.5),2);
    internal static double BaseWidth(int count,double size)=>count*(size+14)+20;

    internal static double AnimationReserve(double size,double magnification)=>
        Math.Ceiling(2*138/(size+14)*size*.78*magnification+(size+10)*magnification+size+14+36);

    internal static double EnclosingWidth(ReadOnlySpan<double> centers,ReadOnlySpan<double> scales,double center,double size)
    {
        double radius=0;
        for(int i=0;i<centers.Length;i++) radius=Math.Max(radius,Math.Abs(centers[i]-center)+(size+10)*scales[i]/2);
        return radius*2+24;
    }

    internal static double Proximity(double pointerY,double surfaceTop,double surfaceHeight)
    {
        // Entering the upper approach band opens the slot continuously, before the pointer
        // reaches the glass. Merely visiting the desktop never reserves a slot.
        double amount=Math.Clamp((pointerY-(surfaceTop-76))/92,0,1);
        if(pointerY>surfaceTop+surfaceHeight) amount*=Math.Clamp(1-(pointerY-surfaceTop-surfaceHeight)/24,0,1);
        return amount*amount*(3-2*amount);
    }

    internal static int InsertionBoundary(double pointerX,double center,int count,double stride,int first=2)
    {
        double start=center-count*stride/2;
        for(int i=first;i<count;i++) if(pointerX<start+(i+.5)*stride) return i;
        return count;
    }

    internal static void Centers(Span<double> centers,ReadOnlySpan<double> scales,double center,double size,int gapIndex,double gap)
    {
        double stride=size+14,total=gap;
        foreach(double scale in scales) total+=stride+(scale-1)*size*.78;
        double x=center-total/2;
        for(int i=0;i<centers.Length;i++)
        {
            if(i==gapIndex) x+=gap;
            double slot=stride+(scales[i]-1)*size*.78;
            centers[i]=x+slot/2; x+=slot;
        }
    }
}
