using System.Windows.Media;

namespace GlassDock.Island;

public enum IslandAccentColor { Default,HermesOrange,KleinBlue,NeonPulseGreen,BerryPink }

internal sealed record IslandColorOption(IslandAccentColor Value,string Name,Brush Swatch)
{
    public override string ToString()=>Name;
}

internal static class IslandPalette
{
    private sealed record Palette(Brush Accent,Brush Sung,Brush Current,Brush Meter);
    private static readonly Palette[] Palettes=[
        Create(Color.FromRgb(209,250,107),Color.FromRgb(225,255,157),Color.FromRgb(224,255,155)),
        Create(Color.FromRgb(255,122,0)),
        Create(Color.FromRgb(0,47,167)),
        Create(Color.FromRgb(57,255,20)),
        Create(Color.FromRgb(232,62,140))
    ];
    internal static IReadOnlyList<IslandColorOption> Options {get;}=new[] {
        new IslandColorOption(IslandAccentColor.Default,"默认",Palettes[0].Accent),
        new IslandColorOption(IslandAccentColor.HermesOrange,"爱马仕橙",Palettes[1].Accent),
        new IslandColorOption(IslandAccentColor.KleinBlue,"克莱因蓝",Palettes[2].Accent),
        new IslandColorOption(IslandAccentColor.NeonPulseGreen,"霓虹脉冲绿",Palettes[3].Accent),
        new IslandColorOption(IslandAccentColor.BerryPink,"莓粉",Palettes[4].Accent)
    };

    internal static IslandAccentColor Validate(IslandAccentColor value)=>Enum.IsDefined(value)?value:IslandAccentColor.Default;
    internal static Brush Accent(IslandAccentColor value)=>Palettes[(int)Validate(value)].Accent;
    internal static Brush Sung(IslandAccentColor value)=>Palettes[(int)Validate(value)].Sung;
    internal static Brush Current(IslandAccentColor value)=>Palettes[(int)Validate(value)].Current;
    internal static Brush Meter(IslandAccentColor value)=>Palettes[(int)Validate(value)].Meter;

    private static Palette Create(Color color,Color? current=null,Color? meter=null)
        =>new(Frozen(color),Frozen(color,.85),Frozen(current??color),Frozen(meter??color));

    private static Brush Frozen(Color color,double opacity=1)
    {
        var brush=new SolidColorBrush(color) {Opacity=opacity};
        brush.Freeze();return brush;
    }
}
