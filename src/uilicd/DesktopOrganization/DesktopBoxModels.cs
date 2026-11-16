namespace GlassDock.DesktopOrganization;

internal sealed record DesktopBoxItem(string Id,string Name,string Path,bool IsDirectory=false);

internal sealed record DesktopBoxDefinition
{
    public string Kind=>"Mapping";
    public bool AutoSize { get; set; }=true;
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Name { get; set; }="桌面收纳";
    public string? DesktopGroup { get; set; }
    public double Left { get; set; }=560;
    public double Top { get; set; }=140;
    public double Width { get; set; }=320;
    public double Height { get; set; }=280;
    public bool Pinned { get; set; }=true;
    public bool Collapsed { get; set; }
    public bool Enabled { get; set; }=true;
    public List<DesktopBoxItem> Items { get; set; }=[];
}

internal sealed record DesktopBoxConfiguration
{
    internal const int MaxBoxes=64;
    public int PresetVersion { get; set; }
    public bool Enabled { get; set; }=true;
    public bool GroupPinned { get; set; }=true;
    public string GroupEdge { get; set; }="Top";
    public double? GroupLeft { get; set; }
    public double? GroupTop { get; set; }
    public List<DesktopBoxDefinition> Boxes { get; set; }=[];
}
