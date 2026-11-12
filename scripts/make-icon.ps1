param([string]$Source,[string]$Destination)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$inputStream=[IO.File]::OpenRead($Source)
try {$image=[Windows.Media.Imaging.BitmapFrame]::Create($inputStream,[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad)} finally {$inputStream.Dispose()}
# Remove only the empty outer margin; preserve the supplied glass artwork.
$factor=$image.PixelWidth/1254.0
$crop=[Windows.Int32Rect]::new([int](150*$factor),[int](130*$factor),[int](950*$factor),[int](950*$factor))
$art=[Windows.Media.Imaging.CroppedBitmap]::new($image,$crop)
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=[Collections.Generic.List[byte[]]]::new()
foreach($size in $sizes) {
    $visual=[Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual,[Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing=$visual.RenderOpen(); try {$drawing.DrawImage($art,[Windows.Rect]::new(0,0,$size,$size))} finally {$drawing.Close()}
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32); $bitmap.Render($visual)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $bytes=[IO.MemoryStream]::new(); try {$encoder.Save($bytes); $frames.Add($bytes.ToArray())} finally {$bytes.Dispose()}
}
$directory=Split-Path -Parent $Destination; [IO.Directory]::CreateDirectory($directory)|Out-Null
$output=[IO.File]::Create($Destination); $writer=[IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset); $offset+=$frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write($frame)}
} finally {$writer.Dispose(); $output.Dispose()}
