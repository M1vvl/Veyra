using System.IO;
using System.Windows;

namespace GlassDock.DesktopOrganization;

internal sealed record DesktopScanResult(IReadOnlyList<DesktopBoxItem> Items,string? Error);

/// <summary>Builds virtual groups from real desktop paths; never modifies their sources.</summary>
internal static class DesktopBoxPresets
{
    internal const int CurrentVersion=1;
    internal const string Applications="applications",Files="files";

    internal static Task<DesktopScanResult> ScanDesktopAsync()=>Task.Run(()=> {
        var scan=Scan(new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) });
        return scan with { Items=scan.Items.Append(DesktopShellReference.CreateRecycleBin()).OrderBy(item=>item.Name,StringComparer.CurrentCultureIgnoreCase).ToArray() };
    });

    internal static DesktopScanResult Scan(IEnumerable<string> desktopDirectories)
    {
        var items=new Dictionary<string,DesktopBoxItem>(StringComparer.OrdinalIgnoreCase);
        var errors=new List<string>();
        foreach(string directory in desktopDirectories.Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if(!Directory.Exists(directory)) continue;
                var options=new EnumerationOptions { RecurseSubdirectories=false,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.Hidden|FileAttributes.System };
                foreach(string entry in Directory.EnumerateFileSystemEntries(directory,"*",options))
                {
                    if(items.Count>=1000) break;
                    if(string.Equals(Path.GetFileName(entry),"desktop.ini",StringComparison.OrdinalIgnoreCase)) continue;
                    string path=Path.GetFullPath(entry);
                    bool isDirectory=Directory.Exists(path);
                    string name=isDirectory?Path.GetFileName(path):Path.GetFileNameWithoutExtension(path);
                    items.TryAdd(path,new(Guid.NewGuid().ToString("N"),name,path,isDirectory));
                }
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(exception.Message); }
        }
        return new(items.Values.OrderBy(item=>item.Name,StringComparer.CurrentCultureIgnoreCase).ToArray(),errors.Count==0?null:string.Join(Environment.NewLine,errors));
    }

    internal static string GroupOf(DesktopBoxItem item)=>!item.IsDirectory && Path.GetExtension(item.Path).ToLowerInvariant() is (".lnk" or ".exe" or ".url")?Applications:Files;

    internal static List<DesktopBoxDefinition> Create(IReadOnlyList<DesktopBoxItem> items)
    {
        var boxes=new List<DesktopBoxDefinition>();
        foreach(string group in new[]{Applications,Files})
        {
            var references=items.Where(item=>GroupOf(item)==group).Take(500).ToList();
            if(references.Count==0) continue;
            boxes.Add(new() { Name=group==Applications?"桌面应用":"桌面文件",DesktopGroup=group,Items=references,Pinned=true,Collapsed=false,Enabled=true });
        }
        if(boxes.Count==0) boxes.Add(new() { Pinned=true,Collapsed=false,Enabled=true });
        return boxes;
    }

    internal static void Arrange(IReadOnlyList<DesktopBoxDefinition> boxes,Rect available,IEnumerable<Rect>? occupied=null)
    {
        if(boxes.Count==0) return;
        // Stored core sizes include 2 DIP beyond each visible glass edge.
        const double gap=DesktopBoxSpacing.Gap-4;
        var used=(occupied??[]).ToList();
        int columns=Math.Min(Math.Max(1,boxes.Count),Math.Max(1,(int)((available.Width+gap)/(220+gap))));
        double width=Math.Clamp((available.Width-gap*(columns-1))/columns,220,360);
        double maximumHeight=Math.Clamp((available.Height-gap*(Math.Ceiling((double)boxes.Count/columns)-1))/Math.Ceiling((double)boxes.Count/columns),130,440);
        foreach(var box in boxes)
        {
            int iconColumns=Math.Max(2,(int)((width-24)/64));
            double height=Math.Clamp(58+Math.Max(1,Math.Ceiling((double)box.Items.Count/iconColumns))*66,130,maximumHeight);
            Rect placement=FindSpace(width,height,available,used,gap);
            box.Width=width; box.Height=height; box.Left=placement.Left; box.Top=placement.Top;
            used.Add(placement);
        }
    }

    private static Rect FindSpace(double width,double height,Rect available,IReadOnlyList<Rect> occupied,double gap)
    {
        for(double top=available.Top;top+height<=available.Bottom+.1;top+=height+gap)
            for(double left=available.Left;left+width<=available.Right+.1;left+=width+gap)
            {
                var candidate=new Rect(left,top,width,height);
                if(!occupied.Any(other=> { var padded=other; padded.Inflate(gap/2,gap/2); return padded.IntersectsWith(candidate); })) return candidate;
            }
        double offset=Math.Min(occupied.Count*22,88);
        return new(Math.Clamp(available.Left+offset,available.Left,Math.Max(available.Left,available.Right-width)),
            Math.Clamp(available.Top+offset,available.Top,Math.Max(available.Top,available.Bottom-height)),width,height);
    }
}
