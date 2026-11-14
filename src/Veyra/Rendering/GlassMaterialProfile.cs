namespace GlassDock.Rendering;

/// <summary>Shared optical presets for windows, internal cards and popup menus.</summary>
internal static class GlassMaterialProfile
{
    internal static GlassOptions Apply(GlassOptions options)
    {
        if(Program.Prefs.GlassEnabled)return options;
        return options with
        {
            ThicknessPixels=options.ThicknessPixels*.9f,
            NormalTransitionPixels=options.NormalTransitionPixels*1.12f,
            Ior=1.40f,
            Dispersion=.002f,
            Roughness=.46f,
            Tint=Math.Max(options.Tint,.045f)
        };
    }
}
