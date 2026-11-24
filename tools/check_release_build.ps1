param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -cne $Commit.ToLowerInvariant()) {
    throw 'The checkout does not match the requested release commit.'
}
$changes = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) {
    throw 'Build releases from a clean, committed checkout.'
}
$project = [xml](Get-Content -LiteralPath (Join-Path $repo 'source/HapticBridge.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$path = (Resolve-Path -LiteralPath $Executable).Path
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
if ($info.FileVersion -cne ($version + '.0') -or $info.ProductVersion -cne ($version + '+' + $revision)) {
    throw 'Executable version or source revision differs from the release checkout. Rebuild after committing.'
}
Write-Output "Release executable matches version $version and commit $revision."
