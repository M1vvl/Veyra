using System.IO;
using System.Text.Json;

namespace GlassDock.DesktopOrganization;

/// <summary>Persists virtual references only. No user item is copied, moved or deleted.</summary>
internal sealed class DesktopBoxStore
{
    private static readonly JsonSerializerOptions JsonOptions=new() { WriteIndented=true };
    private readonly object _writeGate=new();
    private string? _nextJson;
    private Task? _writer;
    private bool _loadFailed;
    internal string FilePath { get; }
    internal string? LastError { get; private set; }
    internal DesktopBoxStore(string? path=null)=>FilePath=path??Path.Combine(Preferences.DataDirectory,"desktop-boxes.json");

    internal DesktopBoxConfiguration Load()
    {
        if(!File.Exists(FilePath)) return new() { Boxes=[new()] };
        try
        {
            var configuration=JsonSerializer.Deserialize<DesktopBoxConfiguration>(File.ReadAllText(FilePath),JsonOptions)??new();
            configuration.PresetVersion=Math.Max(0,configuration.PresetVersion);
            if(configuration.GroupEdge is not ("Top" or "Left" or "Right" or "Free")) configuration.GroupEdge="Top";
            if(configuration.GroupLeft is double x && (!double.IsFinite(x) || Math.Abs(x)>100000))configuration.GroupLeft=null;
            if(configuration.GroupTop is double y && (!double.IsFinite(y) || Math.Abs(y)>100000))configuration.GroupTop=null;
            configuration.Boxes=(configuration.Boxes??[]).Where(box=>box is not null).Take(DesktopBoxConfiguration.MaxBoxes).ToList();
            foreach(var box in configuration.Boxes)
            {
                if(string.IsNullOrWhiteSpace(box.Id)) box.Id=Guid.NewGuid().ToString("N");
                box.Name=string.IsNullOrWhiteSpace(box.Name)?"桌面收纳":box.Name.Trim()[..Math.Min(box.Name.Trim().Length,40)];
                if(box.DesktopGroup is not (DesktopBoxPresets.Applications or DesktopBoxPresets.Files)) box.DesktopGroup=null;
                box.Width=Finite(box.Width,320,220,720); box.Height=Finite(box.Height,280,112,720);
                box.Left=Finite(box.Left,560,-100000,100000); box.Top=Finite(box.Top,140,-100000,100000);
                box.Items=(box.Items??[]).Where(item=>item is not null && DesktopShellReference.IsReference(item.Path))
                    .Select(item=>DesktopShellReference.IsRecycleBin(item.Path)?item with { Path=DesktopShellReference.RecycleBin,IsDirectory=true }:item)
                    .DistinctBy(item=>item.Path,StringComparer.OrdinalIgnoreCase).Take(500).ToList();
            }
            configuration.Boxes=configuration.Boxes.DistinctBy(box=>box.Id,StringComparer.Ordinal).ToList();
            return configuration;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LastError=exception.Message;
            _loadFailed=true;
            return new() { Enabled=false };
        }
    }

    internal Task SaveAsync(DesktopBoxConfiguration configuration)
    {
        if(_loadFailed)return Task.CompletedTask;
        // Serialize on the dispatcher before the background writer observes mutable lists.
        string json=JsonSerializer.Serialize(configuration,JsonOptions);
        lock(_writeGate)
        {
            _nextJson=json;
            return _writer??=Task.Run(WritePending);
        }
    }
    private void WritePending()
    {
        while(true)
        {
            string json;
            lock(_writeGate)
            {
                if(_nextJson is null) { _writer=null; return; }
                json=_nextJson; _nextJson=null;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporary=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
                try { File.WriteAllText(temporary,json); File.Move(temporary,FilePath,true); }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
                LastError=null;
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
            { LastError=exception.Message; Program.Log("Desktop boxes: "+exception.Message); }
        }
    }

    private static double Finite(double value,double fallback,double minimum,double maximum)=>
        double.IsFinite(value)?Math.Clamp(value,minimum,maximum):fallback;
}
