$ErrorActionPreference = 'Stop'
$extension = Join-Path $PSScriptRoot 'extension'
$destination = Join-Path $PSScriptRoot 'dist'
$names = @('manifest.json', 'popup.html', 'popup.css', 'popup.js', 'clock-bridge.js', 'README.txt')
$files = @($names | ForEach-Object { Join-Path $extension $_ })
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing helper build input." } }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$zip = Join-Path $destination 'HapticBridge-Browser-Helper.zip'
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Write-Output $zip
