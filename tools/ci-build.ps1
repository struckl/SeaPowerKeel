<#
.SYNOPSIS
    Builds KeelWatcher.exe and KeelSetup.exe from the public source only: no game files, no files from a developer's PC.

.DESCRIPTION
    What the release builds need from outside this repository, all public: the official BepInEx release (shipped inside KeelSetup.exe,
    unchanged) and the Unity reference assemblies from BepInEx's NuGet feed (only compiled against, never shipped). The results land in
    dist-ci\. This is what .github\workflows\build.yml runs, so a signature can be traced back to this source.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bepInExVersion = '5.4.23.2'
$unityVersion = '6000.0.84'

$work = Join-Path $root 'obj\ci'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$ProgressPreference = 'SilentlyContinue'

$zip = Join-Path $work 'bepinex.zip'
Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$bepInExVersion/BepInEx_win_x64_$bepInExVersion.zip" -OutFile $zip
$bepInEx = Join-Path $work 'bepinex'
Expand-Archive $zip $bepInEx

$nupkg = Join-Path $work 'unity.zip'
Invoke-WebRequest "https://nuget.bepinex.dev/v3/package/unityengine.modules/$unityVersion/unityengine.modules.$unityVersion.nupkg" -OutFile $nupkg
$unity = Join-Path $work 'unity'
Expand-Archive $nupkg $unity
$managed = Join-Path $work 'managed'
New-Item -ItemType Directory -Force $managed | Out-Null
foreach ($name in 'UnityEngine.dll', 'UnityEngine.CoreModule.dll') {
    $found = Get-ChildItem $unity -Recurse -Filter $name | Select-Object -First 1
    if (-not $found) { throw "$name is not in UnityEngine.Modules $unityVersion" }
    Copy-Item $found.FullName $managed
}

$refs = "-p:ManagedDir=$managed", "-p:BepInExCore=$bepInEx\BepInEx\core"
dotnet build (Join-Path $root 'src\SeaPowerKeel.Stub\SeaPowerKeel.Stub.csproj') -c Release -p:Deploy=false @refs
if ($LASTEXITCODE -ne 0) { throw 'Build failed: stub' }
dotnet build (Join-Path $root 'src\SeaPowerKeel.Patcher\SeaPowerKeel.Patcher.csproj') -c Release @refs
if ($LASTEXITCODE -ne 0) { throw 'Build failed: patcher' }
dotnet build (Join-Path $root 'src\SeaPowerKeel.Watcher\SeaPowerKeel.Watcher.csproj') -c Release @refs
if ($LASTEXITCODE -ne 0) { throw 'Build failed: KeelWatcher' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $work 'payload'
$tree = Join-Path $payload 'tree'
New-Item -ItemType Directory -Force (Join-Path $tree 'BepInEx\core') | Out-Null
foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item -LiteralPath (Join-Path $bepInEx $f) $tree }
Copy-Item (Join-Path $bepInEx 'BepInEx\core\*') (Join-Path $tree 'BepInEx\core')
[System.IO.Compression.ZipFile]::CreateFromDirectory($tree, (Join-Path $payload 'bepinex.bin'))
Copy-Item (Join-Path $root 'src\SeaPowerKeel.Stub\bin\Release\netstandard2.1\SeaPowerKeel.Stub.dll') (Join-Path $payload 'stub.bin')
Copy-Item (Join-Path $root 'src\SeaPowerKeel.Patcher\bin\Release\netstandard2.1\SeaPowerKeel.Patcher.dll') (Join-Path $payload 'patcher.bin')
dotnet build (Join-Path $root 'src\SeaPowerKeel.Setup\SeaPowerKeel.Setup.csproj') -c Release "-p:PayloadDir=$payload" @refs
if ($LASTEXITCODE -ne 0) { throw 'Build failed: KeelSetup' }

$out = Join-Path $root 'dist-ci'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root 'src\SeaPowerKeel.Watcher\bin\Release\net48\KeelWatcher.exe') $out
Copy-Item (Join-Path $root 'src\SeaPowerKeel.Setup\bin\Release\net48\KeelSetup.exe') $out
Get-ChildItem $out | ForEach-Object { '{0}  {1}  {2}' -f $_.Name, $_.VersionInfo.ProductName, $_.VersionInfo.ProductVersion }
