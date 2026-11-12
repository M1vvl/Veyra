using System.Windows;

namespace GlassDock.DesktopOrganization;

/// <summary>Preserve manual columns and gaps while following a changed predecessor.</summary>
internal static class DesktopBoxColumnFlow
{
    internal static Dictionary<string,Rect> Reflow(IReadOnlyDictionary<string,Rect> before,
        IReadOnlyDictionary<string,Rect> after,IReadOnlySet<string> resized,double gap)
    {
        var result=after.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        var affected=new HashSet<string>(resized,StringComparer.Ordinal);
        foreach(var child in before.OrderBy(pair=>pair.Value.Top).ThenBy(pair=>pair.Value.Left))
        {
            if(!result.ContainsKey(child.Key))continue;
            var parents=before.Where(pair=>pair.Key!=child.Key && result.ContainsKey(pair.Key)
                && pair.Value.Bottom<=child.Value.Top+.05
                && Math.Min(pair.Value.Right,child.Value.Right)-Math.Max(pair.Value.Left,child.Value.Left)
                    >=Math.Min(pair.Value.Width,child.Value.Width)*.5).ToArray();
            if(parents.Length==0)continue;
            double nearest=parents.Max(pair=>pair.Value.Bottom);
            var preceding=parents.Where(pair=>pair.Value.Bottom>=nearest-.05).ToArray();
            if(!preceding.Any(pair=>affected.Contains(pair.Key)))continue;
            double top=preceding.Max(pair=>result[pair.Key].Bottom+Math.Max(gap,child.Value.Top-pair.Value.Bottom));
            var moved=result[child.Key]; moved.Y=top; result[child.Key]=moved;
            affected.Add(child.Key);
        }
        return result;
    }
}
