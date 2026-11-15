[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$LegacyStub,
    [Parameter(Mandatory)][string]$LegacyLoader,
    [string]$AnchorChainPreloader,
    [string]$AnchorChainLoader,
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$LegacyStub = (Resolve-Path -LiteralPath $LegacyStub).Path
$LegacyLoader = (Resolve-Path -LiteralPath $LegacyLoader).Path
if ([bool]$AnchorChainPreloader -ne [bool]$AnchorChainLoader) {
    throw 'Provide both Anchor Chain DLLs to run the local Anchor Chain case.'
}
if ($AnchorChainLoader) {
    $AnchorChainPreloader = (Resolve-Path -LiteralPath $AnchorChainPreloader).Path
    $AnchorChainLoader = (Resolve-Path -LiteralPath $AnchorChainLoader).Path
}

$repo = Split-Path -Parent $PSScriptRoot
foreach ($component in 'SeaPowerKeel', 'SeaPowerKeel.Stub') {
    & dotnet build (Join-Path $repo "src/$component") -c Release "-p:GameDir=$GameDir" -p:Deploy=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $component." }
}

$newLoader = Join-Path $repo 'src/SeaPowerKeel/bin/Release/netstandard2.1/SeaPowerKeel.dll'
$newStub = Join-Path $repo 'src/SeaPowerKeel.Stub/bin/Release/netstandard2.1/SeaPowerKeel.Stub.dll'
$plugins = Join-Path $GameDir 'BepInEx/plugins'
$keelTarget = Join-Path $plugins 'SeaPowerKeel.Stub.dll'
$anchorTarget = Join-Path $plugins 'AnchorChain.Preloader.dll'
$streaming = Join-Path $GameDir 'Sea Power_Data/StreamingAssets'
$modName = 'KeelCompatibilityTest'
$mod = Join-Path $streaming $modName
$settings = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData/LocalLow/Triassic Games/Sea Power/usersettings.ini'
$bepinLog = Join-Path $GameDir 'BepInEx/LogOutput.log'
$paths = @($keelTarget, $anchorTarget, $settings)

if ((Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $mod)) {
    throw 'Close Sea Power and remove any leftover KeelCompatibilityTest folder before running.'
}
$recoveryMarker = Join-Path (Split-Path -Parent $settings) 'launch_incomplete.marker'
if (Test-Path -LiteralPath $recoveryMarker) {
    throw 'Sea Power has a pending startup recovery prompt. Complete a normal launch before running the matrix.'
}
$original = [IO.File]::ReadAllText($settings)
$section = [regex]::new('(?m)^\[LoadOrder\][^\S\r\n]*\r?\n(?:^(?!\[)[^\r\n]*(?:\r?\n|\z))*')
if (!$section.IsMatch($original)) { throw 'Sea Power must have saved its mod list at least once.' }
$testSettings = $section.Replace($original, "[LoadOrder]$([Environment]::NewLine)Mod1Directory=$modName,True,0$([Environment]::NewLine)NumberOfModFiles=1$([Environment]::NewLine)$([Environment]::NewLine)", 1)

$backup = Join-Path ([IO.Path]::GetTempPath()) "keel-loader-check-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Copy-Item -LiteralPath $path -Destination $backup
    }
}

function Test-Loader($name, $stub, $loader, [switch]$AnchorChain) {
    $stubFile = if ($AnchorChain) { $anchorTarget } else { $keelTarget }
    $loaderName = if ($AnchorChain) { 'AnchorChain.dll' } else { 'SeaPowerKeel.dll' }
    $version = [Reflection.AssemblyName]::GetAssemblyName($loader).Version.ToString(3)
    $boot = if ($AnchorChain) { "Loaded AnchorChain V$version!" } else {
        $stubVersion = [Reflection.AssemblyName]::GetAssemblyName($stub).Version.ToString(3)
        "R.A.T.S. Keel $version (stub $stubVersion)"
    }
    $done = if ($AnchorChain) { $boot } else { '[Info   :R.A.T.S. Keel] Done:' }
    $destination = Join-Path $mod $loaderName
    Copy-Item -LiteralPath $stub -Destination $stubFile -Force
    Copy-Item -LiteralPath $loader -Destination $destination -Force
    [IO.File]::WriteAllText($settings, $testSettings)
    $launch = & $Cli launch --game-dir $GameDir --confirm --timeout 45 --json
    if ($LASTEXITCODE -ne 0) { throw "Sea Power launch failed for $name." }
    $result = $launch | ConvertFrom-Json
    $gameId = $result.processIds[0]
    $launched = [DateTime]$result.launchUtc
    try {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $contents = ''
        do {
            Start-Sleep -Seconds 1
            $file = Get-Item -LiteralPath $bepinLog -ErrorAction SilentlyContinue
            if ($file -and $file.LastWriteTimeUtc -ge $launched.ToUniversalTime()) {
                $contents = Get-Content -LiteralPath $bepinLog -Raw -ErrorAction SilentlyContinue
                if ($null -eq $contents) { $contents = '' }
            }
            if ($contents.Contains($done) -or $contents.Contains('The previous game launch did not finish loading') -or
                !(Get-Process -Id $gameId -ErrorAction SilentlyContinue)) { break }
        } while ((Get-Date) -lt $deadline)
        $capture = Join-Path $backup "$name.log"
        Set-Content -LiteralPath $capture -Value $contents
        if (!$contents.Contains($boot) -or !$contents.Contains($done)) { throw "Loader did not finish for $name. See $capture." }
        Write-Output "PASS $name ($version)"
    }
    finally {
        $game = Get-Process -Id $gameId -ErrorAction SilentlyContinue
        if ($game -and $game.ProcessName -eq 'Sea Power') {
            if (!$game.CloseMainWindow() -or !$game.WaitForExit(15000)) {
                Stop-Process -Id $gameId
                if (!$game.WaitForExit(10000)) { throw "Test process $gameId did not exit." }
            }
        }
        Remove-Item -LiteralPath $stubFile,$destination
    }
}

try {
    foreach ($path in $keelTarget,$anchorTarget) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }
    New-Item -ItemType Directory -Path $mod | Out-Null
    Set-Content -LiteralPath (Join-Path $mod '_info.ini') -Value @('[Language_en]', 'Name=Keel compatibility test')
    Test-Loader 'old-stub-old-loader' $LegacyStub $LegacyLoader
    Test-Loader 'old-stub-new-loader' $LegacyStub $newLoader
    Test-Loader 'new-stub-old-loader' $newStub $LegacyLoader
    Test-Loader 'new-stub-new-loader' $newStub $newLoader
    if ($AnchorChainLoader) {
        Test-Loader 'anchor-chain' $AnchorChainPreloader $AnchorChainLoader -AnchorChain
    }
}
finally {
    if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) {
        throw "Sea Power is still running. Close it and restore the files saved in $backup."
    }
    foreach ($path in $paths) {
        $saved = Join-Path $backup (Split-Path -Leaf $path)
        if (Test-Path -LiteralPath $saved -PathType Leaf) {
            Copy-Item -LiteralPath $saved -Destination $path -Force
        } elseif (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path
        }
    }
    if (Test-Path -LiteralPath $mod) {
        $folder = Get-Item -LiteralPath $mod
        if ($folder.Parent.FullName -ne (Get-Item -LiteralPath $streaming).FullName -or $folder.Name -ne $modName) {
            throw 'Unexpected test folder.'
        }
        Move-Item -LiteralPath $folder.FullName -Destination $backup
    }
    Write-Output "Test logs and original files: $backup"
}
