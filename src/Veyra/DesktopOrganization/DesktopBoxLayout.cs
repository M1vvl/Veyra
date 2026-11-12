using System.Windows;

namespace GlassDock.DesktopOrganization;

/// <summary>Mapping boxes adapt their viewport, without realizing every icon.</summary>
internal static class DesktopBoxLayout
{
    internal const double ColumnStep=64,RowStep=66,MinimumWidth=220,MinimumHeight=112;
    internal static Size ResizeSize(Size desired,Size available)
    {
        int columns=Math.Max(0,(int)Math.Floor((Math.Min(720,available.Width)-MinimumWidth)/ColumnStep));
        int rows=Math.Max(0,(int)Math.Floor((Math.Min(720,available.Height)-MinimumHeight)/RowStep));
        int widthSteps=Math.Clamp((int)Math.Round((desired.Width-MinimumWidth)/ColumnStep,MidpointRounding.AwayFromZero),0,columns);
        int heightSteps=Math.Clamp((int)Math.Round((desired.Height-MinimumHeight)/RowStep,MidpointRounding.AwayFromZero),0,rows);
        return new Size(MinimumWidth+widthSteps*ColumnStep,MinimumHeight+heightSteps*RowStep);
    }
    internal static Size AdaptiveSize(int itemCount,Size available)
    {
        int maxColumns=Math.Max(3,Math.Min(10,(int)((Math.Min(720,available.Width)-28)/ColumnStep)));
        int columns=Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(1,itemCount)*1.7)),3,Math.Max(3,maxColumns));
        double width=28+columns*ColumnStep;
        double height=46+Math.Max(1,Math.Ceiling((double)itemCount/columns))*RowStep;
        return ResizeSize(new Size(width,Math.Max(MinimumHeight,height)),available);
    }
}
