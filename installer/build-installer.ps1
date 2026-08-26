# Build RXDK-VS-Setup.exe with Inno Setup 6.
#
# Builds the RXDK for Visual Studio VSIX, stages the installer payload (the VSIX, the RXDK
# engine extracted from it, and the Xbox MSBuild platform), regenerates the RXDK icon, then
# runs ISCC (installing Inno Setup 6 via winget/choco if it is missing).
param(
    [string]$Configuration = 'Release',
    # Skip building the VSIX and reuse the newest one already in bin\<Configuration>
    # (used by CI, which builds the VSIX in a prior step).
    [switch]$SkipBuild,
    [string]$IssPath = (Join-Path $PSScriptRoot 'setup.iss')
)
$ErrorActionPreference = 'Stop'

$SetupDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PayloadDir = Join-Path $SetupDir 'payload'
$OutputDir = Join-Path $SetupDir 'out'

function Find-Iscc {
    foreach ($c in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function Install-InnoSetup {
    Write-Host 'Inno Setup 6 not found. Installing...' -ForegroundColor Yellow
    $winget = (Get-Command winget -ErrorAction SilentlyContinue).Source
    if ($winget) {
        & $winget install --id JRSoftware.InnoSetup --exact --scope machine `
            --accept-package-agreements --accept-source-agreements --disable-interactivity --silent
        if ($LASTEXITCODE -eq 0) { return }
    }
    $choco = (Get-Command choco -ErrorAction SilentlyContinue).Source
    if ($choco) {
        & $choco install innosetup -y --no-progress
        if ($LASTEXITCODE -eq 0) { return }
    }
    throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isinfo.php (or install winget/Chocolatey) and rerun.'
}

function Ensure-Iscc {
    $iscc = Find-Iscc
    if ($iscc) { return $iscc }
    Install-InnoSetup
    $iscc = Find-Iscc
    if (-not $iscc) { throw 'Inno Setup 6 installed but ISCC.exe was not found. Reopen the terminal and retry.' }
    return $iscc
}

function Resolve-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio 2022+.' }
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($msbuild) -or -not (Test-Path -LiteralPath $msbuild)) {
        throw 'MSBuild.exe not found. Install Visual Studio 2022+ with the C++ workload.'
    }
    return $msbuild
}

# --- version from the vsix manifest ---
$manifest = Join-Path $RepoRoot 'RxdkVs.Package\source.extension.vsixmanifest'
[xml]$mx = Get-Content -LiteralPath $manifest
$ns = New-Object System.Xml.XmlNamespaceManager($mx.NameTable)
$ns.AddNamespace('v', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
$identity = $mx.SelectSingleNode('//v:Identity', $ns)
$version = $identity.Version
Write-Host "RXDK VS extension version: $version" -ForegroundColor Cyan

# --- build the VSIX (unless reusing an existing one) ---
$csproj = Join-Path $RepoRoot 'RxdkVs.Package\RxdkVs.Package.csproj'
if (-not $SkipBuild) {
    Write-Host "Building VSIX ($Configuration)..." -ForegroundColor Cyan
    & (Join-Path $RepoRoot 'scripts\dev.ps1') templates 2>$null   # pack project templates (best-effort)
    $msbuild = Resolve-MSBuild
    & $msbuild $csproj -restore "-p:Configuration=$Configuration" -v:m
    if ($LASTEXITCODE -ne 0) { throw 'msbuild failed building the VSIX.' }
}
else {
    Write-Host "Reusing existing VSIX (SkipBuild)." -ForegroundColor Cyan
}

$binDir = Join-Path $RepoRoot "RxdkVs.Package\bin\$Configuration"
$vsix = Get-ChildItem -LiteralPath $binDir -Filter 'rxdk-vs-*.vsix' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $vsix) { $vsix = Get-ChildItem -LiteralPath $binDir -Filter '*.vsix' | Select-Object -First 1 }
if (-not $vsix) { throw "No .vsix produced in $binDir." }
Write-Host "VSIX: $($vsix.FullName)" -ForegroundColor Green

# --- stage payload ---
if (Test-Path -LiteralPath $PayloadDir) { Remove-Item -LiteralPath $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# 1) the VSIX itself
Copy-Item -LiteralPath $vsix.FullName -Destination (Join-Path $PayloadDir 'rxdk-vs.vsix') -Force

# 2) the engine, taken straight out of the VSIX (a zip) so it matches the extension exactly
Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsixExtract = Join-Path $env:TEMP ('rxdk-vsix-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($vsix.FullName, $vsixExtract)
$engineSrc = Join-Path $vsixExtract 'tools'
if (-not (Test-Path -LiteralPath $engineSrc)) { throw "The VSIX has no bundled engine under tools\ ($engineSrc). Ensure BundleEngine was not disabled." }
$engineDest = Join-Path $PayloadDir 'engine'
Copy-Item -LiteralPath $engineSrc -Destination $engineDest -Recurse -Force
Remove-Item -LiteralPath $vsixExtract -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $engineDest 'Rxdk.Cli.exe'))) {
    throw "Staged engine is missing Rxdk.Cli.exe ($engineDest)."
}

# 3) the Xbox MSBuild platform
$platformSrc = Join-Path $RepoRoot 'RxdkVs.Package\VcPlatform\Platforms\Xbox'
if (-not (Test-Path -LiteralPath $platformSrc)) { throw "Xbox platform files not found ($platformSrc)." }
Copy-Item -LiteralPath $platformSrc -Destination (Join-Path $PayloadDir 'platform') -Recurse -Force

# --- regenerate the RXDK icon from the extension logo ---
& (Join-Path $SetupDir 'make-icon.ps1') `
    -SrcPath (Join-Path $RepoRoot 'RxdkVs.Package\Resources\extension-icon.png') `
    -OutPath (Join-Path $SetupDir 'Icon.ico')

foreach ($req in @('Icon.ico', 'WizardImage.bmp', 'WizardSmallImage.bmp')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SetupDir $req))) { throw "Missing installer asset: $req" }
}

# --- build the installer ---
$iscc = Ensure-Iscc
Write-Host "Building installer with $iscc" -ForegroundColor Cyan
Push-Location -LiteralPath $SetupDir
try {
    & $iscc "/DMyAppVersion=$version" '/DPayloadDir=payload' '/DInstallerOutputDir=out' `
        '/DInstallerOutputBaseName=RXDK-VS-Setup' 'setup.iss'
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

$out = Join-Path $OutputDir 'RXDK-VS-Setup.exe'
if (-not (Test-Path -LiteralPath $out)) { throw "Installer not produced at $out." }
Write-Host "Installer: $out" -ForegroundColor Green

# --- zip the installer ---
# Distribute the .exe inside a .zip so browsers don't flag a bare unsigned
# executable at download time (the "this file isn't commonly downloaded" warning).
$zip = Join-Path $OutputDir 'RXDK-VS-Setup.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zip)) { throw "Installer zip not produced at $zip." }
Write-Host "Installer zip: $zip" -ForegroundColor Green
