# The official BepInEx release that KeelSetup.exe carries, unchanged. Dot-sourced by package.ps1 and ci-build.ps1.
# The zip is checked against a pinned SHA-256, so a build never ships anything but that release: not a copy from a game folder
# (up to 0.5.4 package.ps1 took the developer's game copy, which was a different build of the same version; found 2026-11-13).
$BepInExVersion = '5.4.23.2'
$BepInExSha256 = 'f752ce4e838f4c305b9da1404b6745f2cff23b8bfd494f79f0c84d0a01f59b46'

function Get-OfficialBepInEx([string]$root) {
    $cache = Join-Path $root 'obj\bepinex'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $name = "BepInEx_win_x64_$BepInExVersion.zip"
    $zip = Join-Path $cache $name
    if (-not (Test-Path $zip)) {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$name" -OutFile $zip
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $BepInExSha256) { throw "$name is not the official release (SHA-256 $hash, expected $BepInExSha256). Delete $zip and build again." }
    $dir = Join-Path $cache 'official'
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    Expand-Archive $zip $dir
    return $dir
}

# The tree KeelSetup.exe unpacks into a game without BepInEx, as bepinex.bin (a zip) in $payload.
function New-BepInExPayload([string]$bepInEx, [string]$payload) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tree = Join-Path $payload 'tree'
    New-Item -ItemType Directory -Force (Join-Path $tree 'BepInEx\core') | Out-Null
    foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item -LiteralPath (Join-Path $bepInEx $f) $tree }
    Copy-Item (Join-Path $bepInEx 'BepInEx\core\*') (Join-Path $tree 'BepInEx\core')
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tree, (Join-Path $payload 'bepinex.bin'))
}
