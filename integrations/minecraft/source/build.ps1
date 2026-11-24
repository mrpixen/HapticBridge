param([string]$JdkPath,[string]$MinecraftRoot=(Join-Path $env:APPDATA '.minecraft'),[string]$FabricLibrariesPath,[string]$BuildDirectory)
$ErrorActionPreference='Stop'
$appRoot=Split-Path -Parent $PSScriptRoot
if (-not $JdkPath) { $JdkPath=$env:JAVA_HOME }
if (-not $JdkPath) { throw 'Set -JdkPath or JAVA_HOME to a JDK 25 installation.' }
if (-not $FabricLibrariesPath) { throw 'Set -FabricLibrariesPath to the extracted Fabric API dependency folder; see README.md.' }
if (-not $BuildDirectory) { $BuildDirectory=Join-Path $appRoot 'build' }
foreach($required in @((Join-Path $JdkPath 'bin/javac.exe'),(Join-Path $JdkPath 'bin/jar.exe'),$FabricLibrariesPath)) { if(-not (Test-Path -LiteralPath $required)) { throw 'Missing build dependency. Set -JdkPath and -FabricLibrariesPath; see README.md.' } }
$buildDir=$BuildDirectory
$classes=Join-Path $buildDir ('classes-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $classes -Force | Out-Null
$metadata=Get-Content -LiteralPath (Join-Path $MinecraftRoot 'versions\26.3\26.3.json') -Raw | ConvertFrom-Json
$classpath=@((Join-Path $MinecraftRoot 'versions\26.3\26.3.jar'),(Join-Path $MinecraftRoot 'libraries\net\fabricmc\fabric-loader\0.20.1\fabric-loader-0.20.1.jar'))
foreach ($library in $metadata.libraries) { if ($library.downloads.artifact.path) { $path=Join-Path $MinecraftRoot ('libraries\'+$library.downloads.artifact.path); if(Test-Path -LiteralPath $path) { $classpath+=$path } } }
$classpath+=Get-ChildItem -LiteralPath $FabricLibrariesPath -Filter '*.jar' | ForEach-Object FullName
$classpath+=Join-Path $MinecraftRoot 'libraries\net\fabricmc\sponge-mixin\0.17.5+mixin.0.8.7\sponge-mixin-0.17.5+mixin.0.8.7.jar'
$sources=Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.java' | Where-Object Name -NotLike '*Tests.java' | ForEach-Object FullName
& (Join-Path $JdkPath 'bin\javac.exe') --release 25 -proc:none -encoding UTF-8 -classpath ($classpath -join ';') -d $classes @sources
if($LASTEXITCODE -ne 0) { throw 'Mod compilation failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fabric.mod.json') -Destination $classes -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'immersion.mixins.json') -Destination $classes -Force
$languageDir=Join-Path $classes 'assets\minecraft_immersion\lang'
New-Item -ItemType Directory -Path $languageDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'en_us.json') -Destination $languageDir -Force
& (Join-Path $JdkPath 'bin\jar.exe') --create --file (Join-Path $appRoot 'minecraft-immersion-0.2.1+26.3.jar') -C $classes .
if($LASTEXITCODE -ne 0) { throw 'Mod packaging failed' }
Set-Content -LiteralPath (Join-Path $buildDir 'classpath.txt') -Value ($classpath -join ';')
Write-Output 'Built minecraft-immersion-0.2.1+26.3.jar'
