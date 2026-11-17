param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
& $DotNet run --project (Join-Path $PSScriptRoot 'tests\HapticBridge.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$windowsProject = Join-Path $PSScriptRoot 'tests\windows\HapticBridge.WindowsTests.csproj'
& $DotNet restore $windowsProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Windows test dependency restore failed.' }
& $DotNet run --project $windowsProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Windows clipboard tests failed.' }
$videoProject = Join-Path $PSScriptRoot 'tests\windows\video\HapticBridge.VideoPipelineTests.csproj'
& $DotNet restore $videoProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Video pipeline test dependency restore failed.' }
& $DotNet run --project $videoProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Video pipeline tests failed.' }
$studioProject = Join-Path $PSScriptRoot 'tests\windows\studio\HapticBridge.StudioTests.csproj'
& $DotNet restore $studioProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Studio test dependency restore failed.' }
& $DotNet run --project $studioProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Studio integration tests failed.' }
