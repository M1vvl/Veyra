using System.Text.Json.Serialization;

namespace GlassDock.Models;

/// <summary>A pin is data only. Loading it never launches a program.</summary>
public sealed record PinnedApp
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Application";
    /// <summary>The original shell item; launching a .lnk retains its shell metadata.</summary>
    public string Path { get; init; } = "";
    public string Target { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string IconPath { get; init; } = "";
    public int IconIndex { get; init; }
    public string AppUserModelId { get; init; } = "";

    [JsonIgnore]
    public string Identity => string.Join("|", !string.IsNullOrWhiteSpace(AppUserModelId) ? AppUserModelId :
            !string.IsNullOrWhiteSpace(Target) ? Target : Path, Arguments)
        .ToUpperInvariant();
}

public sealed record WindowSnapshot(nint Handle, uint ProcessId, string Title, string ExecutablePath,
    string AppUserModelId = "");
