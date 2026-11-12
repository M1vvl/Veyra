using System.Windows;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxManager
{
    internal void SetCollapsed(DesktopBoxDefinition box,bool collapsed)=>SetCollapsed(new[]{box},collapsed);
    internal void SetCollapsed(IEnumerable<DesktopBoxDefinition> boxes,bool collapsed)
    {
        var changed=boxes.Where(box=>Configuration.Boxes.Contains(box) && box.Collapsed!=collapsed).ToArray();
        if(changed.Length==0)return;
        ClaimConfiguration();
        var before=_windows.Values.Where(window=>window.IsLoaded).ToDictionary(window=>window.BoxId,window=>window.VisibleBoundsPixels);
        foreach(var box in changed)
        {
            box.Collapsed=collapsed;
            if(_windows.TryGetValue(box.Id,out var window))window.RefreshModel();
        }
        var after=_windows.Values.Where(window=>window.IsLoaded).ToDictionary(window=>window.BoxId,window=>window.VisibleBoundsPixels);
        double dpi=_windows.Values.FirstOrDefault(window=>window.IsLoaded) is { } first?VisualTreeHelper.GetDpi(first).DpiScaleY:1;
        var flowed=DesktopBoxColumnFlow.Reflow(before,after,changed.Select(box=>box.Id).ToHashSet(StringComparer.Ordinal),DesktopBoxSpacing.Gap*dpi);
        var moves=new List<DesktopWindowMove>();
        foreach(var pair in flowed)
            if(_windows.TryGetValue(pair.Key,out var window) && (pair.Value.TopLeft-after[pair.Key].TopLeft).Length>.05)
                moves.Add(new(window,window.PlanDragBounds(pair.Value)));
        if(moves.Count>0 && !DesktopWindowMotion.Apply(moves.ToArray()))
            foreach(var pair in flowed)if(_windows.TryGetValue(pair.Key,out var window))window.ApplyDragBounds(pair.Value);
        // Existing group placement owns monitor/control boundaries and collisions.
        _group?.EnsureSpacing(); Save(); Changed?.Invoke(this,EventArgs.Empty);
    }
}
