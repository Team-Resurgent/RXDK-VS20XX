<#
.SYNOPSIS
Restores media files a sample loads by name but that are absent from its tree, sourcing them
from the retail XDK 5849 sample install.

.DESCRIPTION
Test-SampleMedia.ps1 reports the gaps; this fills them. For each missing file the donor is
chosen in order:

  1. the same sample's Media tree in the XDK install (the authoritative copy), then
  2. a sibling XDK sample that ships a file of that name, preferring the nearest one by
     directory distance so the dolphin family feeds the dolphin samples and so on.

Shader outputs (.xvu/.xpu) are never copied when the donor also ships the .vsh/.psh source --
the build assembles those itself, so taking the source keeps the sample buildable from scratch.

Runs read-only unless -Apply is given.
#>
[CmdletBinding()]
param(
    [string] $Root,
    [string] $XdkSamples = 'D:\Git\RXDK\POC\XDKSetup5849.17\XDK\Samples\Xbox',
    [switch] $Apply
)

# Assets the XDK ships for titles to redistribute rather than for one sample: the UIX sound
# banks and the stock DSP effects images live here, and nothing under Samples has them.
$xdkRoot = Split-Path (Split-Path $XdkSamples -Parent) -Parent

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SampleMedia.ps1')

if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$Root = (Resolve-Path -LiteralPath $Root).Path
$samplesRoot = Join-Path $Root 'XDKSamples'

if (-not (Test-Path $XdkSamples)) { throw "XDK sample tree not found: $XdkSamples" }

# Every candidate donor, indexed by leaf name. Our own tree counts too: the audio samples
# share one set of wavs and effects images, so a sample that ships them can feed one that
# does not, and for several files that is the only copy anywhere.
$donors = @{}
$donorRoots = @(
    $XdkSamples
    (Join-Path $xdkRoot 'redist')
    (Join-Path $xdkRoot 'Source')
    $samplesRoot
) | Where-Object { Test-Path $_ }
foreach ($root in $donorRoots) {
    foreach ($f in Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue) {
        if ($f.FullName -match '\\out\\|\\bin\\|\\obj\\') { continue }
        if ($f.Extension -notmatch '^\.(xbg|xvu|xpu|vsh|psh|wav|wma|bin|uix|xsb|xwb|xmv|ttf|bmp|tga|xpr)$') { continue }
        $k = $f.Name.ToLowerInvariant()
        if (-not $donors.ContainsKey($k)) { $donors[$k] = New-Object System.Collections.ArrayList }
        [void]$donors[$k].Add($f.FullName)
    }
}

# Directory distance between two sample paths: how much of the path they share.
function Get-Affinity([string] $a, [string] $b) {
    $x = $a.ToLowerInvariant() -split '[\\/]'
    $y = $b.ToLowerInvariant() -split '[\\/]'
    $n = 0
    while ($n -lt $x.Count -and $n -lt $y.Count -and $x[$n] -eq $y[$n]) { $n++ }
    return $n
}

$plan = @()

$manifests = Get-ChildItem $samplesRoot -Recurse -Filter rxdk.project.json |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|\\out\\' }

foreach ($m in $manifests) {
    $projectRoot = Split-Path $m.FullName -Parent
    $sampleDir = Split-Path $projectRoot -Parent
    $name = (Get-Content $m.FullName -Raw | ConvertFrom-Json).name

    $wanted = Get-SampleWantedMedia -SampleDir $sampleDir

    # The matching sample in the XDK install, by the same relative path.
    $rel = $sampleDir.Substring($samplesRoot.Length).TrimStart('\')
    $xdkSampleDir = Join-Path $XdkSamples $rel

    foreach ($w in ($wanted.Keys | Sort-Object)) {
        $dest = Join-Path (Join-Path $sampleDir 'Media') ($w -replace '/', '\')
        if (Test-Path $dest) { continue }

        $leaf = Split-Path $w -Leaf
        $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)
        $isShader = $leaf -match '\.(xvu|xpu)$'
        $srcExt = if ($leaf -match '\.xvu$') { '.vsh' } else { '.psh' }

        # A .vsh already in our own tree means the build will assemble it; nothing to copy.
        if ($isShader -and (Test-Path (Join-Path (Split-Path $dest -Parent) "$stem$srcExt"))) { continue }

        # Prefer the shader source over the assembled output.
        $keys = if ($isShader) { @("$stem$srcExt".ToLowerInvariant(), $leaf.ToLowerInvariant()) }
                else { @($leaf.ToLowerInvariant()) }

        $pick = $null
        foreach ($k in $keys) {
            if (-not $donors.ContainsKey($k)) { continue }
            $pick = $donors[$k] |
                Sort-Object @{ Expression = { Get-Affinity $xdkSampleDir $_ }; Descending = $true }, Length |
                Select-Object -First 1
            if ($pick) { break }
        }

        $plan += [pscustomobject]@{
            Sample = $name
            Wanted = $w
            Donor  = $pick
            Dest   = if ($pick) { Join-Path (Split-Path $dest -Parent) (Split-Path $pick -Leaf) } else { $null }
        }
    }
}

$found = $plan | Where-Object Donor
$lost = $plan | Where-Object { -not $_.Donor }

# Donors come from several roots, so trim against whichever one this file was found under.
function Get-DonorLabel([string] $path) {
    foreach ($root in ($donorRoots | Sort-Object Length -Descending)) {
        if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            return "{0}: {1}" -f (Split-Path $root -Leaf), $path.Substring($root.Length).TrimStart('\')
        }
    }
    return $path
}

"resolved $($found.Count) file(s); $($lost.Count) with no donor"
''
foreach ($p in $found | Sort-Object Sample, Wanted) {
    "{0,-22} {1,-26} <- {2}" -f $p.Sample, $p.Wanted, (Get-DonorLabel $p.Donor)
}
if ($lost) {
    ''
    '--- no donor anywhere ---'
    foreach ($p in $lost | Sort-Object Sample, Wanted) { "{0,-22} {1}" -f $p.Sample, $p.Wanted }
}

if ($Apply) {
    ''
    foreach ($p in $found) {
        $dir = Split-Path $p.Dest -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Copy-Item -LiteralPath $p.Donor -Destination $p.Dest -Force
        # The XDK install tree is read-only and Copy-Item carries the attribute over. Staging
        # then cannot overwrite the file, and the ISO pack skips with only a note, so the title
        # boots against a stale image and still reports the media as missing.
        (Get-Item -LiteralPath $p.Dest).IsReadOnly = $false
    }
    "copied $($found.Count) file(s)"
}
