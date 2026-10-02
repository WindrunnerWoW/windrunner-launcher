<#
.SYNOPSIS
  Recreate the repository's artifacts directory and publish a Windows build into it.

.PARAMETER Aot
  NativeAOT publish (slower; needs the VS C++ workload). Off by default for local iteration.

.PARAMETER Configuration
  dotnet configuration. Default: Release.
#>
param(
    [switch] $Aot,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifacts = Join-Path $repoRoot "artifacts"
$project = Join-Path $repoRoot "src\WindrunnerLauncher.App\WindrunnerLauncher.App.csproj"

if (-not (Test-Path $project)) {
    throw "App project not found: $project"
}

# The launcher, and anything it started (MariaDB, mangosd, realmd, mariadb-install-db), keeps files
# under artifacts open. A crashed launcher leaves those children running on their own.
$running = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Stopping process(es) running from ${artifacts}:"
    $running | ForEach-Object { Write-Host "  $($_.Name) (pid $($_.Id))" }
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
}

Write-Host "Clearing $artifacts"
if (Test-Path $artifacts) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}
New-Item -ItemType Directory -Force $artifacts | Out-Null

$aotValue = if ($Aot) { "true" } else { "false" }
Write-Host "Publishing WindrunnerLauncher ($Configuration, win-x64, PublishAot=$aotValue) -> $artifacts"

dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    -p:PublishAot=$aotValue `
    -o $artifacts

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $artifacts "WindrunnerLauncher.exe"
if (-not (Test-Path $exe)) {
    throw "Publish succeeded but WindrunnerLauncher.exe was not written to $artifacts"
}

Write-Host "Done: $exe"
Get-ChildItem $artifacts | Format-Table Name, Length
