<#
.SYNOPSIS
Compares two BenchmarkDotNet result sets (full JSON exports) and flags regressions.

.DESCRIPTION
Matches benchmark cases by their full name (type, method and parameters) and compares
the median time and allocated bytes per operation. Storage-backed benchmarks are much
noisier than in-memory ones, so they get a wider time tolerance. Exits with code 1 when
any case regressed beyond its tolerance or is missing from the candidate run.

.EXAMPLE
./benchmarks/Compare-BenchmarkResults.ps1 `
    -Baseline benchmarks/baselines/2026-11-06-csharp-net11 `
    -Candidate benchmarks/baselines/2027-01-15-csharp-net11
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Baseline,
    [Parameter(Mandatory)] [string] $Candidate,
    [double] $CpuTimeTolerancePercent = 10,
    [double] $IoTimeTolerancePercent = 30,
    [double] $AllocationTolerancePercent = 10,
    # Allocation differences smaller than this are ignored, so a few extra bytes on a
    # tiny allocation do not trip the percentage tolerance.
    [long] $AllocationToleranceBytes = 256,
    # Benchmarks whose full name matches this pattern touch the filesystem or SQLite.
    [string] $IoBoundPattern = 'BackupExecutorBenchmarks|BackupPipelineBenchmarks|BackupPlannerBenchmarks|ContentStoreBufferBenchmarks|DirectoryScanBenchmarks|FromFile'
)

$ErrorActionPreference = 'Stop'

function Read-Results([string] $Path) {
    $files = Get-ChildItem -Path $Path -Recurse -Filter '*-report-full.json'
    if (-not $files) {
        throw "No '*-report-full.json' files found under '$Path'. Run the benchmarks with '--exporters fulljson'."
    }

    $results = @{}
    foreach ($file in $files) {
        $report = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json
        foreach ($benchmark in $report.Benchmarks) {
            $results[$benchmark.FullName] = [pscustomobject]@{
                MedianNs       = [double] $benchmark.Statistics.Median
                AllocatedBytes = [long] $benchmark.Memory.BytesAllocatedPerOperation
                Runtime        = $report.HostEnvironmentInfo.RuntimeVersion
            }
        }
    }

    return $results
}

function Format-Time([double] $Nanoseconds) {
    if ($Nanoseconds -ge 1e6) { return '{0:N2} ms' -f ($Nanoseconds / 1e6) }
    if ($Nanoseconds -ge 1e3) { return '{0:N2} us' -f ($Nanoseconds / 1e3) }
    return '{0:N1} ns' -f $Nanoseconds
}

function Get-ChangePercent([double] $Before, [double] $After) {
    if ($Before -eq 0) { return 0 }
    return ($After - $Before) / $Before * 100
}

$baselineResults = Read-Results $Baseline
$candidateResults = Read-Results $Candidate

$rows = foreach ($name in ($baselineResults.Keys + $candidateResults.Keys | Sort-Object -Unique)) {
    $before = $baselineResults[$name]
    $after = $candidateResults[$name]
    $displayName = $name -replace '^Vara\.Benchmarks\.', ''

    if (-not $after) {
        [pscustomobject]@{ Status = 'MISSING'; Benchmark = $displayName; Time = ''; TimeChange = ''; Allocated = ''; AllocChange = '' }
        continue
    }

    if (-not $before) {
        [pscustomobject]@{ Status = 'NEW'; Benchmark = $displayName; Time = Format-Time $after.MedianNs; TimeChange = ''; Allocated = "$($after.AllocatedBytes) B"; AllocChange = '' }
        continue
    }

    $timeTolerance = if ($name -match $IoBoundPattern) { $IoTimeTolerancePercent } else { $CpuTimeTolerancePercent }
    $timeChange = Get-ChangePercent $before.MedianNs $after.MedianNs
    $allocDelta = $after.AllocatedBytes - $before.AllocatedBytes
    $allocChange = Get-ChangePercent $before.AllocatedBytes $after.AllocatedBytes

    $timeRegressed = $timeChange -gt $timeTolerance
    $allocRegressed = $allocDelta -gt $AllocationToleranceBytes -and $allocChange -gt $AllocationTolerancePercent

    $status = if ($timeRegressed -or $allocRegressed) { 'REGRESSED' }
        elseif ($timeChange -lt -$timeTolerance) { 'IMPROVED' }
        else { 'OK' }

    [pscustomobject]@{
        Status      = $status
        Benchmark   = $displayName
        Time        = '{0} -> {1}' -f (Format-Time $before.MedianNs), (Format-Time $after.MedianNs)
        TimeChange  = '{0:+0.0;-0.0;0.0}% (tol {1}%)' -f $timeChange, $timeTolerance
        Allocated   = '{0} -> {1} B' -f $before.AllocatedBytes, $after.AllocatedBytes
        AllocChange = '{0:+0.0;-0.0;0.0}%' -f $allocChange
    }
}

$rows | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Write-Output

$summary = $rows | Group-Object Status | ForEach-Object { '{0}: {1}' -f $_.Name, $_.Count }
Write-Output ('Summary - ' + ($summary -join ', '))

$runtimes = @(
    ($baselineResults.Values | Select-Object -First 1).Runtime,
    ($candidateResults.Values | Select-Object -First 1).Runtime
)
if ($runtimes[0] -ne $runtimes[1]) {
    Write-Warning "Runtimes differ: baseline '$($runtimes[0])', candidate '$($runtimes[1])'. Differences may not be caused by the code change alone."
}

if ($rows | Where-Object { $_.Status -in 'REGRESSED', 'MISSING' }) {
    exit 1
}
