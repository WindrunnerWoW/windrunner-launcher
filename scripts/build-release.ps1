<#
.SYNOPSIS
  Build the Windows release the way the Release workflow's release-win-x64 job does: restore, test,
  NativeAOT publish with ProductionTrust (JIT single-file fallback when AOT fails), then stage the exe,
  SHA-256 files and launcher payload with scripts/stage-windows-build.ps1.

  Not covered (CI only): the Linux AppImage, signing launcher.json (needs the LAUNCHER_SIGNING_KEY secret)
  and the GitHub Release itself.

.PARAMETER Version
  Semver to stamp. Default: <Version> from Directory.Build.props, like the workflow.

.PARAMETER OutDir
  Staged release (exe, .sha256, SHA256SUMS.txt, hashes.json, launcher.json, BUILD.txt). Recreated.
  Default: artifacts/release. Relative paths resolve from the repository root.

.PARAMETER PublishDir
  Raw publish output; recreated. Default: artifacts/release-publish.

.PARAMETER SkipTests
  Skip `dotnet test` (the workflow always runs it).

.PARAMETER NoFallback
  Fail when NativeAOT fails instead of building the self-contained JIT single file.

.PARAMETER ExeUrl
  Download URL written to launcher.json. Omit for a local build.

.PARAMETER Notes
  Optional notes copied into launcher.json.
#>
param(
    [string] $Version = "",
    [string] $OutDir = "artifacts/release",
    [string] $PublishDir = "artifacts/release-publish",
    [switch] $SkipTests,
    [switch] $NoFallback,
    [string] $ExeUrl = "",
    [string] $Notes = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-Dir([string] $path) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw "Directory parameters must not be empty." }
    $full = if ([System.IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repoRoot $path }
    return [System.IO.Path]::GetFullPath($full).TrimEnd([char[]]@('\', '/'))
}

$out = Resolve-Dir $OutDir
$publish = Resolve-Dir $PublishDir
$solution = Join-Path $repoRoot "WindrunnerLauncher.sln"
$project = Join-Path $repoRoot "src\WindrunnerLauncher.App\WindrunnerLauncher.App.csproj"

if (-not $Version) {
    $props = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw
    if ($props -notmatch "<Version>([^<]+)</Version>") { throw "Directory.Build.props has no <Version>." }
    $Version = $Matches[1]
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}

$commit = ""
try { $commit = (git -C $repoRoot rev-parse HEAD 2>$null).Trim() } catch { }

function Invoke-Dotnet {
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE" }
}

Write-Host "Release $Version (commit $commit)"

Write-Host "Restore"
Invoke-Dotnet restore $solution

if (-not $SkipTests) {
    Write-Host "Test"
    Invoke-Dotnet test $solution -c Release --no-restore
}

# A running launcher from the publish folder keeps its files open.
$running = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($publish + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Stopping process(es) running from ${publish}:"
    $running | ForEach-Object { Write-Host "  $($_.Name) (pid $($_.Id))" }
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
}

function New-CleanDir([string] $path) {
    if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Force $path | Out-Null
}

# NativeAOT first, exactly the workflow's properties.
New-CleanDir $publish
Write-Host "Publish (NativeAOT, win-x64) -> $publish"
dotnet publish $project -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:ProductionTrust=true `
    -p:Version=$Version `
    -o $publish
$nativeAot = ($LASTEXITCODE -eq 0)

if (-not $nativeAot) {
    if ($NoFallback) { throw "NativeAOT publish failed (exit code $LASTEXITCODE)." }
    Write-Warning "NativeAOT publish failed: falling back to a self-contained single-file (JIT) publish."
    New-CleanDir $publish
    Invoke-Dotnet publish $project -c Release -r win-x64 `
        -p:PublishAot=false `
        -p:ProductionTrust=true `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version `
        -o $publish
}

if (-not (Test-Path (Join-Path $publish "WindrunnerLauncher.exe"))) {
    throw "Publish succeeded but WindrunnerLauncher.exe was not written to $publish"
}

if (-not $Notes) {
    $Notes = if ($nativeAot) { "NativeAOT win-x64 build." } else { "Self-contained JIT fallback; NativeAOT publish failed." }
}

Write-Host "Stage -> $out"
& (Join-Path $PSScriptRoot "stage-windows-build.ps1") `
    -SourceDir $publish `
    -OutDir $out `
    -Version $Version `
    -Commit $commit `
    -NativeAot $nativeAot `
    -ExeUrl $ExeUrl `
    -Notes $Notes

Write-Host ""
Write-Host "Done ($(if ($nativeAot) { 'NativeAOT' } else { 'JIT fallback' })): $(Join-Path $out 'WindrunnerLauncher.exe')"
Get-ChildItem $out | Format-Table Name, Length
