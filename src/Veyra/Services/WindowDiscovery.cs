using GlassDock.Models;

namespace GlassDock.Services;

internal sealed record WindowDiscoverySnapshot(IReadOnlyList<WindowSnapshot> Windows, IReadOnlyList<PinnedApp> Running);

/// <summary>Shell property stores and file version handlers can block without a deadline.
/// Keep all discovery on one background worker and retain the last completed UI snapshot.</summary>
internal sealed class WindowDiscovery
{
    private readonly SingleFlightOperation<WindowDiscoverySnapshot> _operation = new();
    private readonly Func<IReadOnlyList<PinnedApp>,WindowDiscoverySnapshot> _scan;
    internal WindowDiscovery(Func<IReadOnlyList<PinnedApp>,WindowDiscoverySnapshot>? scan=null)=>_scan=scan??Scan;
    internal Task<WindowDiscoverySnapshot> ReadAsync(IReadOnlyList<PinnedApp> pins) => _operation.StartOrJoin(()=>_scan(pins));
    private static WindowDiscoverySnapshot Scan(IReadOnlyList<PinnedApp> pins)
    {
        WindowService.RefreshDirectoryTargets(pins);
        var windows = WindowService.GetAllWindows();
        return new WindowDiscoverySnapshot(windows, WindowService.GetRunningApps(windows));
    }
}
