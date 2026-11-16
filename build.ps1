param([string]$DotnetPath='dotnet',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'scripts\build-uilicd.ps1') -DotnetPath $DotnetPath -OutputDirectory $OutputDirectory
