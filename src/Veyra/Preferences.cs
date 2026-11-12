using System.IO;
using System.Text.Json;

namespace GlassDock;

public sealed class Preferences
{
    public bool ReplaceTaskbar { get; set; } = true;
    public bool SidebarPinned { get; set; }
    public bool ReducedMotion { get; set; }
    public bool GlassEnabled { get; set; } = true;
    public double GlassTransparency { get; set; }
    public int GlassFramesPerSecond { get; set; } = 30;
    public double Magnification { get; set; } = .68;
    public double IconSize { get; set; } = 46;
    public double DockScale { get; set; } = .65;
    public bool HideInFullscreen { get; set; } = true;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDock");
    private static string FilePath => Path.Combine(DataDirectory, "preferences.json");
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath))?.Validated() ?? new(); }
        catch { return new(); }
    }
    public Preferences Validated()
    {
        GlassFramesPerSecond = Math.Clamp(GlassFramesPerSecond, 1, 60);
        GlassTransparency=double.IsFinite(GlassTransparency)?Math.Clamp(GlassTransparency,0,1):0;
        Magnification = Math.Clamp(Magnification, 0, .85);
        IconSize = Math.Clamp(IconSize, 32, 64);
        DockScale = Math.Clamp(DockScale, .35, 1);
        return this;
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(Validated(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
