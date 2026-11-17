param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
& $DotNet run --project (Join-Path $PSScriptRoot 'tests\HapticBridge.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
