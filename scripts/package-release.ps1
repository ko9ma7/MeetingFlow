param(
    [string]$Version = '2.2.1'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$packageName = "MeetingFlow-v$Version-win-x64"
$package = Join-Path $artifacts $packageName
$zip = Join-Path $artifacts "$packageName.zip"

if (Test-Path $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}

New-Item -ItemType Directory -Path $package | Out-Null
dotnet publish (Join-Path $root 'MeetingFlow.App\MeetingFlow.App.csproj') -c Release -r win-x64 --self-contained false -o $package

# Public packages do not include debug symbols because they can expose local
# build paths and are not required to run the application.
Get-ChildItem -LiteralPath $package -Filter '*.pdb' -Recurse | Remove-Item -Force

$packageScripts = Join-Path $package 'scripts'
$packageDocs = Join-Path $package 'docs'
New-Item -ItemType Directory -Path $packageScripts,$packageDocs | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts\setup-python.ps1') -Destination $packageScripts
Copy-Item -LiteralPath (Join-Path $root 'scripts\install-meetingflow.ps1') -Destination $packageScripts
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'docs\USAGE.md') -Destination $packageDocs
Copy-Item -LiteralPath (Join-Path $root 'docs\SPEAKER_DIARIZATION_SETUP.md') -Destination $packageDocs
Copy-Item -LiteralPath (Join-Path $root 'docs\STT_SERVICE_COMPARISON.md') -Destination $packageDocs
Copy-Item -LiteralPath (Join-Path $root 'docs\RELEASE_NOTES_2.2.1.md') -Destination $packageDocs

Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
Get-Item $zip
