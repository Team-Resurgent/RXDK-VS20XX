# Builds every project with an rxdk.project.json and reports the failures.
# A blunt regression check for changes that reach all titles (SDK libs, link recipe).
# Windows PowerShell 5.1 compatible: throttles plain child processes rather than
# using ForEach-Object -Parallel.
param(
    [string] $Root = (Split-Path $PSScriptRoot -Parent),
    [int] $Throttle = 8,
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$cli = (Get-ChildItem (Join-Path $Root "Rxdk.Cli\bin") -Recurse -Filter Rxdk.Cli.exe |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $cli) { throw "Rxdk.Cli.exe not found; run 'dotnet build Rxdk.Cli' first." }

$manifests = Get-ChildItem $Root -Recurse -Filter rxdk.project.json |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|\\out\\' }

"Building $($manifests.Count) projects with $cli"

$logDir = Join-Path $env:TEMP "rxdk-sweep"
Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $logDir -ItemType Directory -Force | Out-Null

$running = New-Object System.Collections.ArrayList
$done = New-Object System.Collections.ArrayList

# Moves every finished child from $running to $done, sampling HasExited once per
# process so that one exiting mid-pass cannot fall out of both lists.
function Drain-Finished {
    for ($i = $running.Count - 1; $i -ge 0; $i--) {
        if ($running[$i].Proc.HasExited) {
            [void]$done.Add($running[$i])
            $running.RemoveAt($i)
        }
    }
}

foreach ($m in $manifests) {
    while ($running.Count -ge $Throttle) {
        Drain-Finished
        if ($running.Count -ge $Throttle) { Start-Sleep -Milliseconds 200 }
    }

    $projectRoot = Split-Path $m.FullName -Parent
    $name = (Get-Content $m.FullName -Raw | ConvertFrom-Json).name
    $log = Join-Path $logDir ("{0}.log" -f ($m.FullName -replace '[\\:]', '_'))
    $p = Start-Process -FilePath $cli -NoNewWindow -PassThru `
        -ArgumentList @("build", "--project-root", $projectRoot, "--configuration", $Configuration) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    # Touching Handle caches the process handle; without it Windows PowerShell
    # closes it on exit and ExitCode comes back empty for every child.
    $null = $p.Handle
    [void]$running.Add([pscustomobject]@{ Name = $name; Root = $projectRoot; Log = $log; Proc = $p })
}

while ($running.Count -gt 0) {
    Drain-Finished
    if ($running.Count -gt 0) { Start-Sleep -Milliseconds 200 }
}

$failed = @($done | Where-Object { $_.Proc.ExitCode -ne 0 })
"OK: $($done.Count - $failed.Count) / $($done.Count)"
foreach ($f in $failed) {
    ""
    "=== FAILED: $($f.Name)  ($($f.Root))"
    Get-Content $f.Log -Tail 20 -ErrorAction SilentlyContinue
    Get-Content "$($f.Log).err" -Tail 20 -ErrorAction SilentlyContinue
}
if ($failed.Count -gt 0) { exit 1 }
