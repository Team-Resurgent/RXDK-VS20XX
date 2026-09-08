#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Developer / test helper for RXDK-VS20XX.

.DESCRIPTION
  One entry point for the fiddly iterate loop: rebuild + republish the net8 engine and
  debug adapter into %ProgramData%\RXDK\engine (where an installed VSIX and the sample
  .vcxproj files resolve them), build the sample solution, deploy a title to the kit,
  smoke-test the DAP adapter, build the VSIX, and (re)generate a debug launch config.

.EXAMPLE
  ./scripts/dev.ps1 status              # show kit IP, tool/SDK/Zig status, engine files
  ./scripts/dev.ps1 publish             # rebuild Rxdk.Cli + Rxdk.Dap -> ProgramData\RXDK\engine
  ./scripts/dev.ps1 samples             # msbuild the sample .vcxproj solution
  ./scripts/dev.ps1 deploy -Sample Game # build+deploy a sample to the kit
  ./scripts/dev.ps1 smoke               # DAP initialize/disconnect handshake test on the adapter
  ./scripts/dev.ps1 launch-json -Sample Game   # (re)write rxdk-debug.launch.json for that sample
  ./scripts/dev.ps1 vsix                # msbuild the RxdkVs.Package VSIX
  ./scripts/dev.ps1 all                 # publish + samples (the usual after a code change)
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'publish', 'samples', 'deploy', 'run', 'reboot', 'smoke', 'vsix', 'templates',
        'install', 'uninstall', 'reinstall', 'launch-json', 'all', 'help')]
    [string]$Command = 'status',

    [ValidateSet('Game', 'Empty', 'Lib', 'Dxt')]
    [string]$Sample = 'Game',

    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Debug'
)

$ErrorActionPreference = 'Stop'

# ---- paths ----
$Repo       = Split-Path -Parent $PSScriptRoot          # scripts/ sits under the repo root
$EngineDir  = Join-Path $env:ProgramData 'RXDK\engine'
$Cli        = Join-Path $EngineDir 'Rxdk.Cli.exe'
$Dap        = Join-Path $EngineDir 'Rxdk.Dap.exe'
$SamplesSln = Join-Path $Repo 'samples\RXDK-Samples.sln'
$VsixProj   = Join-Path $Repo 'RxdkVs.Package\RxdkVs.Package.csproj'
# The net8 build engine (Rxdk.Cli/Rxdk.Dap/Rxdk.Engine) is the RXDK-Tools submodule.
$EngineSrc  = Join-Path $Repo 'external\RXDK-Tools\src'

# Ensure the engine submodule is present and, so a locally-built VSIX bundles the newest engine,
# fast-forwarded to the tip of RXDK-Tools' default branch (mirrors the CI "latest tools" step).
function Update-EngineSubmodule {
    Info "Updating engine submodule (external/RXDK-Tools) to latest tools"
    & git -C $Repo submodule update --init --remote --recursive external/RXDK-Tools
    if ($LASTEXITCODE -ne 0) { throw "git submodule update failed for external/RXDK-Tools" }
}

function Info($m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)    { Write-Host "OK  $m" -ForegroundColor Green }
function Warn($m)  { Write-Host "!!  $m" -ForegroundColor Yellow }

function Get-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found ($vswhere) - is Visual Studio installed?" }
    $msb = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msb) { throw "MSBuild not found via vswhere." }
    return $msb
}

function Get-SampleDir  { Join-Path $Repo "samples\$Sample" }
function Get-SampleName {
    $manifest = Join-Path (Get-SampleDir) 'rxdk.project.json'
    if (-not (Test-Path $manifest)) { throw "No rxdk.project.json in $(Get-SampleDir)" }
    (Get-Content $manifest -Raw | ConvertFrom-Json).name
}

# ---- commands ----

function Invoke-Publish {
    Update-EngineSubmodule
    Info "Publishing Rxdk.Cli + Rxdk.Dap (net8, framework-dependent) -> $EngineDir"
    # A running VS / debug session can lock the exes; warn rather than fail cryptically.
    foreach ($p in 'Rxdk.Cli', 'Rxdk.Dap') {
        if (Get-Process $p -ErrorAction SilentlyContinue) {
            Warn "$p is running - close it (or the debug session using it) if publish fails with a file lock."
        }
    }
    foreach ($proj in 'Rxdk.Cli', 'Rxdk.Dap') {
        $csproj = Join-Path $EngineSrc "$proj\$proj.csproj"
        dotnet publish $csproj -c Release -o $EngineDir --no-self-contained -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $proj" }
    }
    Ok "Engine published. Rxdk.Cli.exe / Rxdk.Dap.exe are current."
}

function Invoke-Samples {
    Info "Building sample solution ($Config|Xbox)"
    $msb = Get-MSBuild
    & $msb -nologo -v:m -restore "-p:Configuration=$Config;Platform=Xbox" $SamplesSln
    if ($LASTEXITCODE -ne 0) { throw "sample build failed" }
    Ok "Samples built."
}

function Invoke-Deploy {
    Info "Building+deploying '$Sample' to the kit"
    # Build via MSBuild so the platform's RxdkGenerateProjectJson target writes rxdk.project.json
    # from the .vcxproj; deploy then reads it and selects this configuration's per-config output.
    $msb = Get-MSBuild
    & $msb -nologo -v:m "-p:Configuration=$Config;Platform=Xbox" (Join-Path (Get-SampleDir) "$Sample.vcxproj")
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    & $Cli deploy --project-root (Get-SampleDir) --configuration $Config
    if ($LASTEXITCODE -ne 0) { throw "deploy failed" }
    Ok "Deployed '$Sample'."
}

function Invoke-Run    { Info "Launching '$Sample' on the kit"; & $Cli run --project-root (Get-SampleDir) }
function Invoke-Reboot { Info "Warm-rebooting the kit"; & $Cli reboot }

function Invoke-Smoke {
    Info "DAP handshake smoke test against $Dap"
    if (-not (Test-Path $Dap)) { throw "Rxdk.Dap.exe not found - run: ./scripts/dev.ps1 publish" }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Dap
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)

    function Send($obj) {
        $json = $obj | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetByteCount($json)
        $proc.StandardInput.Write("Content-Length: $bytes`r`n`r`n$json")
        $proc.StandardInput.Flush()
    }
    Send @{ seq = 1; type = 'request'; command = 'initialize'; arguments = @{ adapterID = 'xbox' } }
    Start-Sleep -Milliseconds 800
    Send @{ seq = 2; type = 'request'; command = 'disconnect'; arguments = @{} }
    Start-Sleep -Milliseconds 600
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(3000)) { $proc.Kill() }
    $out = $proc.StandardOutput.ReadToEnd()

    $hasInit = $out -match '"command"\s*:\s*"initialize"'
    $hasEvt  = $out -match '"event"\s*:\s*"initialized"'
    if ($hasInit -and $hasEvt) {
        Ok "Adapter responds to DAP (initialize + initialized event). The exe is healthy."
    }
    else {
        Warn "Unexpected adapter output - the DAP handshake did not complete:"
        Write-Host $out
        throw "smoke test failed"
    }
}

function Invoke-Templates {
    Info "Packing VS project templates"
    $srcRoot = Join-Path $Repo 'RxdkVs.Package\TemplateSrc'
    $outRoot = Join-Path $Repo 'RxdkVs.Package\ProjectTemplates'
    # TemplateSrc folder name -> display .zip name shown in File > New.
    $names = @{
        Game            = 'Original Xbox Game'
        Empty           = 'Original Xbox Empty'
        Lib             = 'Original Xbox Lib'
        Dxt             = 'Original Xbox DXT'
        ControllerInput = 'Original Xbox Controller Input'
        FontScroller    = 'Original Xbox Font Scroller'
        NetworkServer   = 'Original Xbox Network Server'
        VideoPlayer     = 'Original Xbox Video Player'
        Cube            = 'Original Xbox Cube (Multi-Project)'
        MusicVisualizer = 'Original Xbox Music Visualizer (Multi-Project)'
    }
    if (-not (Test-Path $outRoot)) { New-Item -ItemType Directory -Path $outRoot | Out-Null }
    foreach ($dir in Get-ChildItem -Path $srcRoot -Directory -ErrorAction SilentlyContinue) {
        $display = if ($names.ContainsKey($dir.Name)) { $names[$dir.Name] } else { $dir.Name }
        $zip = Join-Path $outRoot "$display.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $dir.FullName '*') -DestinationPath $zip
        Ok "  $($dir.Name) -> $display.zip"
    }

    # NOTE: the RXDK MSBuild integration (props/targets + property-page rule XMLs) is no longer
    # copied per-project. It lives in the installed "Xbox" platform
    # (RxdkVs.Package\VcPlatform\Platforms\Xbox), which the VSIX ships and the "Install Xbox
    # Platform" command copies into VCTargetsPath. Projects inherit it from Platform=Xbox, so
    # there is no Scaffold\ staging step here anymore.
}

function Invoke-Vsix {
    Update-EngineSubmodule
    Invoke-Templates
    Info "Building RxdkVs.Package VSIX"
    $msb = Get-MSBuild
    & $msb -nologo -v:m -restore "-p:Configuration=Debug" $VsixProj
    if ($LASTEXITCODE -ne 0) { throw "VSIX build failed" }
    $built = Get-ChildItem (Join-Path $Repo 'RxdkVs.Package\bin\Debug') -Filter 'rxdk-vs-*.vsix' -EA SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Ok ("VSIX built -> {0}" -f ($(if ($built) { $built.FullName } else { 'RxdkVs.Package\bin\Debug\RxdkVs.Package.vsix' })))
}

# The install-capable VSIX: prefer the product-branded rxdk-vs-<version>.vsix, else the raw one.
function Get-BuiltVsix {
    $dir = Join-Path $Repo 'RxdkVs.Package\bin\Debug'
    $branded = Get-ChildItem $dir -Filter 'rxdk-vs-*.vsix' -EA SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($branded) { return $branded.FullName }
    return (Join-Path $dir 'RxdkVs.Package.vsix')
}

function Get-VsixInstaller {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $root = & $vswhere -latest -property installationPath | Select-Object -First 1
    if (-not $root) { throw "Visual Studio installation not found via vswhere." }
    $exe = Join-Path $root 'Common7\IDE\VSIXInstaller.exe'
    if (-not (Test-Path $exe)) { throw "VSIXInstaller.exe not found ($exe)." }
    return $exe
}

function Get-VsInstanceIds {
    # The VSIX targets [17.0,19.0), so install/uninstall across EVERY instance in that range
    # (VS 2022 + VS 2026), not just -latest -- otherwise the older IDE keeps a stale extension.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found ($vswhere)." }
    $ids = & $vswhere -all -prerelease -version '[17.0,19.0)' -property instanceId
    $ids = @($ids | Where-Object { $_ })
    if ($ids.Count -eq 0) { throw "No Visual Studio 2022/2026 instance found via vswhere." }
    return $ids
}

function Get-ExtensionId {
    $manifest = Join-Path $Repo 'RxdkVs.Package\source.extension.vsixmanifest'
    $m = Select-String -Path $manifest -Pattern '<Identity[^>]*Id="([^"]+)"' | Select-Object -First 1
    if (-not $m) { throw "Could not read the extension Identity Id from the manifest." }
    return $m.Matches[0].Groups[1].Value
}

function Assert-VsClosed {
    if (Get-Process devenv -ErrorAction SilentlyContinue) {
        Warn "Visual Studio (devenv) is running. Close ALL VS windows first - VSIXInstaller cannot update a loaded extension."
        throw "Close Visual Studio, then retry."
    }
}

function Invoke-Uninstall {
    Assert-VsClosed
    $installer = Get-VsixInstaller
    $id = Get-ExtensionId
    # Per-instance (not one batched /instanceIds call): a batch aborts if the extension is absent
    # from any listed instance (code 2003), which would skip the instances that DO have it.
    foreach ($iid in Get-VsInstanceIds) {
        Info "Uninstalling extension $id from $iid"
        & $installer "/uninstall:$id" "/instanceIds:$iid" /quiet | Out-Null
        # 0 = ok; ~2003 = not installed on this instance. Either is fine for a reinstall flow.
        if ($LASTEXITCODE -eq 0) { Ok "  uninstalled from $iid" }
        else { Warn "  nothing to uninstall from $iid (code $LASTEXITCODE) - continuing" }
    }
}

function Invoke-Install {
    Assert-VsClosed
    $installer = Get-VsixInstaller
    $vsix = Get-BuiltVsix
    if (-not (Test-Path $vsix)) { throw "VSIX not built yet - run: ./scripts/dev.ps1 vsix" }
    $failed = @()
    foreach ($iid in Get-VsInstanceIds) {
        Info "Installing $vsix -> $iid"
        & $installer "/instanceIds:$iid" $vsix /quiet | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok "  installed to $iid" }
        else { $failed += "$iid (code $LASTEXITCODE)" }
    }
    if ($failed.Count -gt 0) { throw "VSIXInstaller failed for: $($failed -join '; ')" }
    Ok "Installed. Start Visual Studio to use it."
}

function Invoke-Reinstall {
    Assert-VsClosed
    Invoke-Vsix          # build (packs templates first)
    Invoke-Uninstall
    Invoke-Install
    Ok "Reinstall complete - launch VS."
}

function Write-LaunchJson {
    $name = Get-SampleName
    $dir  = Get-SampleDir
    $out  = Join-Path $dir 'out'
    $json = [ordered]@{
        '$adapter'        = $Dap
        type              = 'xbox'
        request           = 'launch'
        name              = "Debug $name"
        program           = (Join-Path $out "$name.exe")
        pdb               = (Join-Path $out "$name.pdb")
        xbePath           = "xe:\$name\$name.xbe"
        '__workspaceFolder' = $dir
        reboot            = $false
    }
    $path = Join-Path $dir 'rxdk-debug.launch.json'
    $json | ConvertTo-Json | Set-Content -Path $path -Encoding utf8
    Ok "Wrote $path"
    Write-Host ""
    Write-Host "In the VS Command Window (Ctrl+Alt+A):" -ForegroundColor Cyan
    Write-Host "  DebugAdapterHost.Logging /On /OutputWindow"
    Write-Host "  DebugAdapterHost.Launch /LaunchJson:`"$path`""
}

function Show-Status {
    Info "Kit / toolchain status"
    if (Test-Path $Cli) {
        & $Cli xbox-ip
        Write-Host ""
        & $Cli tools-status
        Write-Host ""
        & $Cli sdk-status
        Write-Host ""
        & $Cli zig-status
    }
    else {
        Warn "Rxdk.Cli.exe not found in $EngineDir - run: ./scripts/dev.ps1 publish"
    }
    Write-Host ""
    Info "Engine files in $EngineDir"
    if (Test-Path $EngineDir) {
        Get-ChildItem $EngineDir -Filter '*.exe' | Select-Object Name, @{n='Modified';e={$_.LastWriteTime}} | Format-Table -AutoSize
    } else { Warn "engine dir missing" }
}

function Show-Help { Get-Help $PSCommandPath -Detailed }

switch ($Command) {
    'status'      { Show-Status }
    'publish'     { Invoke-Publish }
    'samples'     { Invoke-Samples }
    'deploy'      { Invoke-Deploy }
    'run'         { Invoke-Run }
    'reboot'      { Invoke-Reboot }
    'smoke'       { Invoke-Smoke }
    'vsix'        { Invoke-Vsix }
    'templates'   { Invoke-Templates }
    'install'     { Invoke-Install }
    'uninstall'   { Invoke-Uninstall }
    'reinstall'   { Invoke-Reinstall }
    'launch-json' { Write-LaunchJson }
    'all'         { Invoke-Publish; Invoke-Samples }
    'help'        { Show-Help }
}
