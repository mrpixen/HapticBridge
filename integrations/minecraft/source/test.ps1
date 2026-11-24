param([string]$JdkPath,[string]$GsonPath,[string]$MinecraftRoot=(Join-Path $env:APPDATA '.minecraft'),[string]$BuildDirectory)
$ErrorActionPreference='Stop'
if (-not $JdkPath) { $JdkPath=$env:JAVA_HOME }
if (-not $JdkPath) { throw 'Set -JdkPath or JAVA_HOME to a JDK 25 installation.' }
if(-not $BuildDirectory) { $BuildDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'build/tests' }
if(-not $GsonPath) { $GsonPath=Get-ChildItem -LiteralPath (Join-Path $MinecraftRoot 'libraries/com/google/code/gson/gson') -Recurse -Filter '*.jar' | Select-Object -Last 1 -ExpandProperty FullName }
foreach($required in @((Join-Path $JdkPath 'bin/javac.exe'),(Join-Path $JdkPath 'bin/java.exe'),$GsonPath)) { if(-not $required -or -not (Test-Path -LiteralPath $required)) { throw 'Missing test dependency. Set -JdkPath and -GsonPath; see README.md.' } }
$classes=Join-Path $BuildDirectory ('classes-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $classes -Force | Out-Null
& (Join-Path $JdkPath 'bin\javac.exe') --release 25 -encoding UTF-8 -d $classes (Join-Path $PSScriptRoot 'HapticEngine.java') (Join-Path $PSScriptRoot 'EngineTests.java')
if($LASTEXITCODE -ne 0) { throw 'Engine test compilation failed' }
& (Join-Path $JdkPath 'bin\java.exe') -cp $classes io.pixen.immersion.EngineTests
if($LASTEXITCODE -ne 0) { throw 'Engine tests failed' }
$gson=$GsonPath
& (Join-Path $JdkPath 'bin\javac.exe') --release 25 -encoding UTF-8 -cp ($classes+';'+$gson) -d $classes (Join-Path $PSScriptRoot 'ImmersionConfig.java') (Join-Path $PSScriptRoot 'ConfigTests.java')
if($LASTEXITCODE -ne 0) { throw 'Config test compilation failed' }
& (Join-Path $JdkPath 'bin\java.exe') -cp ($classes+';'+$gson) io.pixen.immersion.ConfigTests (Join-Path $classes 'migration-test.json')
if($LASTEXITCODE -ne 0) { throw 'Config tests failed' }

& (Join-Path $JdkPath 'bin\javac.exe') --release 25 -encoding UTF-8 -cp ($classes+';'+$gson) -d $classes (Join-Path $PSScriptRoot 'GatewayBridge.java') (Join-Path $PSScriptRoot 'GatewayTests.java')
if($LASTEXITCODE -ne 0) { throw 'Gateway test compilation failed' }
& (Join-Path $JdkPath 'bin\java.exe') -cp ($classes+';'+$gson) io.pixen.immersion.GatewayTests
if($LASTEXITCODE -ne 0) { throw 'Gateway tests failed' }
