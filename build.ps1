param([string]$DotnetPath='dotnet',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'scripts\build-veyra.ps1') -DotnetPath $DotnetPath -OutputDirectory $OutputDirectory
