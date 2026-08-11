param(
    [string]$Source = (Split-Path -Parent $PSScriptRoot),
    [switch]$SetUpPython
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [System.IO.Path]::GetFullPath($Source)
$localAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
$installRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'Programs\MeetingFlow'))

if (-not $installRoot.StartsWith($localAppData, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved installation path is outside LocalAppData.'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'MeetingFlow.exe'))) {
    throw "MeetingFlow.exe was not found: $sourceRoot"
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
if (-not $sourceRoot.Equals($installRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -Path (Join-Path $sourceRoot '*') -Destination $installRoot -Recurse -Force
}

$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MeetingFlow.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installRoot 'MeetingFlow.exe'
$shortcut.WorkingDirectory = $installRoot
$shortcut.Description = 'MeetingFlow AI Meeting Notes'
$shortcut.Save()

if ($SetUpPython) {
    & (Join-Path $installRoot 'scripts\setup-python.ps1') -SkipCrisper
}

Write-Host "Installed: $installRoot"
Write-Host "Desktop shortcut: $shortcutPath"
