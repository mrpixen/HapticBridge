[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\HapticBridge'),
    [string]$StartMenuDirectory = [Environment]::GetFolderPath('Programs')
)
$ErrorActionPreference = 'Stop'
$source = Get-Item -LiteralPath $Executable
if ($source.PSIsContainer -or $source.Extension -ne '.exe' -or $source.VersionInfo.ProductName -ne 'HapticBridge') {
    throw 'Choose a built or downloaded HapticBridge executable.'
}
$installPath = [IO.Path]::GetFullPath($InstallDirectory)
$menuPath = [IO.Path]::GetFullPath($StartMenuDirectory)
$target = Join-Path $installPath 'HapticBridge.exe'
$shortcutPath = Join-Path $menuPath 'HapticBridge.lnk'
$sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
if (!$PSCmdlet.ShouldProcess($target, 'Install HapticBridge and create its Start menu shortcut')) { return }

# Do not stop an active session or replace an executable while it is running.
foreach ($process in Get-Process -Name HapticBridge -ErrorAction SilentlyContinue) {
    if ($process.Path -eq $target) { throw 'Close HapticBridge before updating it.' }
}
New-Item -ItemType Directory -Path $installPath -Force | Out-Null
New-Item -ItemType Directory -Path $menuPath -Force | Out-Null
if ($source.FullName -ne $target) {
    $staged = Join-Path $installPath ('.update-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Copy-Item -LiteralPath $source.FullName -Destination $staged
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Copied executable failed verification.' }
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($staged, $target, [NullString]::Value) }
        else { [IO.File]::Move($staged, $target) }
    }
    finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged }
    }
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $installPath
$shortcut.IconLocation = "$target,0"
$shortcut.Description = 'HapticBridge — device controls and game feedback'
$shortcut.Save()
if ($shell.CreateShortcut($shortcutPath).TargetPath -ne $target) { throw 'Start menu shortcut verification failed.' }
Write-Output "Installed HapticBridge $($source.VersionInfo.ProductVersion)"
Write-Output "Start menu: HapticBridge"
Write-Output "Executable: $target"
# Settings stay in the existing user data folder. Installation never launches output.
