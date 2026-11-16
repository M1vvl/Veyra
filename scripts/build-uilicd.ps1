param(
    [string]$DotnetPath='dotnet',
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if($DotnetPath -eq 'dotnet') {
    $localSdk=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if(Test-Path -LiteralPath $localSdk) {$DotnetPath=$localSdk}
}
$projectDirectory=Join-Path $projectRoot 'src\uilicd'
if(-not (Test-Path -LiteralPath $projectDirectory)) {$projectDirectory=Join-Path $projectRoot 'src\GlassDock'}
$projectPath=Join-Path $projectDirectory 'uilicd.csproj'
if(-not (Test-Path -LiteralPath $projectPath)) {$projectPath=Join-Path $projectDirectory 'GlassDock.csproj'}
if(-not $OutputDirectory) {$OutputDirectory=Join-Path $projectRoot 'dist'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& $DotnetPath build $projectPath -c Release -p:RestoreIgnoreFailedSources=true
if($LASTEXITCODE -ne 0) {throw 'uilicd bootstrap build failed.'}
$bootstrap=Join-Path $projectDirectory 'bin\Release\net8.0-windows10.0.19041.0\uilicd.dll'
& $DotnetPath $bootstrap --compile-glass-shaders (Join-Path $projectDirectory 'Rendering\Shaders')
if($LASTEXITCODE -ne 0) {throw 'uilicd optical shader compilation failed.'}
# WPF and WinRT require their native libraries and reflection metadata intact.
# Keep the bundle uncompressed to avoid a recurring decompression memory cost.
& $DotnetPath publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -p:RestoreIgnoreFailedSources=true -o $OutputDirectory
if($LASTEXITCODE -ne 0) {throw 'uilicd single-file publish failed.'}
Write-Host "Built: $(Join-Path $OutputDirectory 'uilicd.exe')"
