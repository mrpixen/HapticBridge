param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$DotNet = 'dotnet',
    [switch]$Locked
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'source\HapticBridge.csproj'
$destination = Join-Path $PSScriptRoot (Join-Path 'dist' $Runtime)
$restoreArguments = @('restore', $project)
if ($Locked) { $restoreArguments += '--locked-mode' }
& $DotNet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
& $DotNet publish $project --no-restore --configuration Release --runtime $Runtime --self-contained true --output $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$files = @(Get-ChildItem -LiteralPath $destination -Force)
if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -ne 'HapticBridge.exe') { throw 'Expected exactly one HapticBridge.exe and no other files or folders in the publish directory.' }
Get-FileHash -LiteralPath (Join-Path $destination 'HapticBridge.exe') -Algorithm SHA256
Write-Output "Published single executable: $destination\HapticBridge.exe"
