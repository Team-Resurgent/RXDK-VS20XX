<#
.SYNOPSIS
Reports media files a sample loads by name at runtime but that are missing from its ISO.

.DESCRIPTION
A title asks for its media by literal path (m_Mesh.Create("Models\\Airplane.xbg"),
m_Font.Create("Font.xpr"), ...). Nothing at build time checks those names against what
actually gets staged into the image, so a sample links and packs cleanly and then dies in
Initialize() on the first missing file. This scrapes the literals out of each sample's
sources and diffs them against the packed media tree.

A missing file does not always surface as XBAPPERR_MEDIANOTFOUND: several samples wrap the
loader's result in a bare E_FAIL, so the failure arrives with no indication that media is
what went wrong. That makes this diff the cheaper way to find them.
#>
[CmdletBinding()]
param(
    [string] $Root,
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SampleMedia.ps1')

if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$Root = (Resolve-Path -LiteralPath $Root).Path
$samplesRoot = Join-Path $Root 'XDKSamples'

$manifests = Get-ChildItem $samplesRoot -Recurse -Filter rxdk.project.json |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|\\out\\' }

$rows = @()

foreach ($m in $manifests) {
    $projectRoot = Split-Path $m.FullName -Parent
    $name = (Get-Content $m.FullName -Raw | ConvertFrom-Json).name
    $packed = Join-Path $projectRoot "out\$Configuration\Build\$name\media"
    if (-not (Test-Path $packed)) { continue }

    # what actually shipped, keyed by lowercase path relative to the media root
    $present = @{}
    foreach ($f in Get-ChildItem $packed -Recurse -File) {
        $rel = $f.FullName.Substring($packed.Length).TrimStart('\').Replace('\', '/').ToLowerInvariant()
        $present[$rel] = $true
    }

    # sources live beside the .vcproj, one level above the project dir
    $sampleDir = Split-Path $projectRoot -Parent
    $wanted = Get-SampleWantedMedia -SampleDir $sampleDir

    $missing = @()
    foreach ($k in $wanted.Keys) { if (-not $present.ContainsKey($k)) { $missing += $wanted[$k] } }

    if ($missing.Count) {
        $rows += [pscustomobject]@{
            Sample  = $name
            Missing = ($missing | Sort-Object) -join ', '
        }
    }
}

"Samples with media referenced in code but absent from the ISO: $($rows.Count)"
''
$rows | Sort-Object Sample | ForEach-Object { "{0,-24} {1}" -f $_.Sample, $_.Missing }
