<#
.SYNOPSIS
  Copy a published Windows launcher into a release/CI staging folder and emit SHA-256 files.

.PARAMETER SourceDir
  Directory produced by `dotnet publish` (contains WindrunnerLauncher.exe).

.PARAMETER OutDir
  Staging directory. Recreated.

.PARAMETER Version
  Semver stamped into launcher.json and BUILD.txt.

.PARAMETER Commit
  Git SHA recorded in BUILD.txt.

.PARAMETER NativeAot
  Whether this build used PublishAot=true.

.PARAMETER ExeUrl
  Public download URL for launcher.json. Omit on CI artifacts that are not a GitHub Release.

.PARAMETER Notes
  Optional release notes copied into launcher.json.
#>
param(
    [Parameter(Mandatory = $true)][string] $SourceDir,
    [Parameter(Mandatory = $true)][string] $OutDir,
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $Commit = "",
    [bool] $NativeAot = $true,
    [string] $ExeUrl = "",
    [string] $Notes = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SourceDir)) {
    throw "Publish directory not found: $SourceDir"
}

$exe = Get-ChildItem -Path $SourceDir -Filter "WindrunnerLauncher.exe" -File | Select-Object -First 1
if (-not $exe) {
    throw "WindrunnerLauncher.exe was not produced in $SourceDir"
}

if (Test-Path $OutDir) {
    Remove-Item -Recurse -Force $OutDir
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

# The exe embeds Skia/ANGLE and unpacks them beside itself on first run. Stage
# only WindrunnerLauncher.exe so GitHub Releases are a single download.
Copy-Item -LiteralPath $exe.FullName -Destination (Join-Path $OutDir $exe.Name)

$stagedExe = Join-Path $OutDir "WindrunnerLauncher.exe"
if (-not (Test-Path $stagedExe)) {
    throw "WindrunnerLauncher.exe was not copied to $OutDir"
}

if (Test-Path (Join-Path $PSScriptRoot "..\LICENSE")) {
    Copy-Item (Join-Path $PSScriptRoot "..\LICENSE") (Join-Path $OutDir "LICENSE")
}
if (Test-Path (Join-Path $PSScriptRoot "..\README.md")) {
    Copy-Item (Join-Path $PSScriptRoot "..\README.md") (Join-Path $OutDir "README.md")
}

$hash = (Get-FileHash $stagedExe -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $stagedExe).Length
$stamp = [DateTime]::UtcNow.ToString("o")

$hashedFiles = Get-ChildItem -Path $OutDir -File |
    Where-Object { $_.Extension -in ".exe", ".dll" } |
    Sort-Object Name |
    ForEach-Object {
        $fileHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [ordered]@{
            name   = $_.Name
            sha256 = $fileHash
            size   = $_.Length
        }
    }

$sumsPath = Join-Path $OutDir "SHA256SUMS.txt"
$sidecarPath = Join-Path $OutDir "WindrunnerLauncher.exe.sha256"
# GNU sha256sum format (two spaces). ASCII, no BOM.
$sumLines = ($hashedFiles | ForEach-Object { "$($_.sha256)  $($_.name)" }) -join "`n"
[System.IO.File]::WriteAllText($sumsPath, $sumLines + "`n", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($sidecarPath, "$hash  WindrunnerLauncher.exe`n", [System.Text.UTF8Encoding]::new($false))

$hashes = [ordered]@{
    algorithm = "SHA-256"
    version   = $Version
    commit    = $Commit
    nativeAot = $NativeAot
    createdUtc = $stamp
    files     = @($hashedFiles)
}
$hashesJson = $hashes | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $OutDir "hashes.json"), $hashesJson + "`n", [System.Text.UTF8Encoding]::new($false))

$buildKind = if ($NativeAot) { "NativeAOT" } else { "self-contained single-file (JIT fallback)" }
$buildTxt = @"
Windrunner Launcher
version:    $Version
commit:     $Commit
kind:       $buildKind
rid:        win-x64
sha256:     $hash
size:       $size
createdUtc: $stamp
"@
[System.IO.File]::WriteAllText((Join-Path $OutDir "BUILD.txt"), $buildTxt.Trim() + "`n", [System.Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($ExeUrl)) {
    $manifest = [ordered]@{
        schema  = 1
        version = $Version
        exeUrl  = $ExeUrl
        sha256  = $hash
        size    = $size
    }
    if (-not [string]::IsNullOrWhiteSpace($Notes)) {
        $manifest.notes = $Notes
    }
    $manifestJson = ($manifest | ConvertTo-Json -Depth 6)
    [System.IO.File]::WriteAllText((Join-Path $OutDir "launcher.json"), $manifestJson + "`n", [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Staged $stagedExe"
Write-Host "SHA-256 $hash  ($size bytes, $buildKind)"
Get-ChildItem $OutDir | Format-Table Name, Length
Get-Content $sumsPath
