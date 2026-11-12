<#
.SYNOPSIS
    Optional release check: measures the published Native AOT vara.exe against the recorded baseline.

.DESCRIPTION
    Runs `backup --json` against fixtures on the real filesystem and compares the process's CPU time, peak working set
    and the executable's size with the figures recorded in a baseline folder (release-check.json). Use -Record to write
    that file from the current build instead of comparing.

    Scenarios (all under -ScratchDirectory):
      1. first backup of a fixture of 2,000 files of 1 to 64 KB;
      2. a second backup of the same source with nothing changed;
      3. first backup of a large tree of 4,000 files of 1 to 4 KB in 200 directories.

    Each run starts from a fresh target; the first run of each scenario is a warm-up that is discarded. Per sample the
    script records wall-clock time around the process, the process's CPU time (user plus kernel) and the peak working
    set; it also records the size of the executable.

    Rules:
      - CPU time (asserted): the median and the fastest run are each at most 1.10 times the baseline's;
      - memory (asserted): the peak working set of the large-tree scenario is at most 1.10 times the baseline's (the
        others are recorded);
      - executable size (asserted): at most 1.05 times the baseline's;
      - wall-clock time (advisory): a median and a fastest run that are both above 1.15 times the baseline's are flagged
        "investigate", never failed, because timing on a machine with antimalware scanning is noisy;
      - a scenario whose CPU time has an interquartile range above 5% of its median is inconclusive: its CPU rule is
        reported and does not decide the verdict. Run the check again on a quiet machine.
    Allocations are held by the BenchmarkDotNet baseline (budget 5%) and are not remeasured here.

.PARAMETER Exe
    The published Native AOT vara.exe (dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli). Copy it into
    a folder that is excluded from Windows Defender scanning (for example under -ScratchDirectory) and pass that copy:
    the antimalware scan of a freshly built executable is part of the process's measured time.

.PARAMETER BaselineDirectory
    The baseline folder that holds release-check.json (for example benchmarks/baselines/2026-11-12-fsharp-net11).

.PARAMETER Record
    Write release-check.json to the baseline folder from this run, and do not compare.

.PARAMETER ScratchDirectory
    Required: where fixtures and targets are created, in the subfolders `fixtures` and `work`. Use a dedicated directory
    on a local drive, and one that is excluded from Windows Defender scanning: real-time scanning inflates CPU time and
    makes it noisy, and the result's Antimalware line only reports whether protection is on, not whether this folder
    is excluded. The directory differs between machines, so confirm it before recording a baseline. The script prints
    the resolved path when it starts. `work` is deleted and recreated on every run; `fixtures` is kept between runs.

.PARAMETER RunCount
    Runs per scenario, the first being the discarded warm-up. Default 5.

.PARAMETER SmallFileCount
    Files of the small fixture (scenarios 1 and 2). Default 2000.

.PARAMETER LargeFileCount
    Files of the large tree (scenario 3). Default 4000, in 200 directories.

.PARAMETER OutputDirectory
    When given, writes release-check.md (the table, machine and verdict) there.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $BaselineDirectory,
    [Parameter(Mandatory)] [string] $ScratchDirectory,
    [switch] $Record,
    [int] $RunCount = 5,
    [int] $SmallFileCount = 2000,
    [int] $LargeFileCount = 4000,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Numbers in the report use a point and no group separator, whatever the machine's culture.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
if ($RunCount -lt 3) { throw 'RunCount must be at least 3: one warm-up and two samples.' }

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$baselineFile = Join-Path $BaselineDirectory 'release-check.json'
if (-not $Record -and -not (Test-Path -LiteralPath $baselineFile)) {
    throw "No baseline at $baselineFile. Record one with -Record."
}

# ---------------------------------------------------------------- fixtures

function Remove-Tree([string] $path) {
    if (Test-Path -LiteralPath $path) {
        # Hard-linked mirror entries are read-only by design, so the attribute goes first. cmd's rmdir is far faster
        # than Remove-Item on a hundred thousand files.
        & attrib.exe -r "$path\*" /s /d 2>&1 | Out-Null
        & cmd.exe /c "rmdir /s /q `"$path`"" 2>&1 | Out-Null
        if (Test-Path -LiteralPath $path) { throw "Could not remove $path" }
    }
}

function New-Fixture([string] $root, [int] $fileCount, [int] $directoryCount, [int] $minBytes, [int] $maxBytes, [int] $seed) {
    Remove-Tree $root
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $random = [System.Random]::new($seed)
    $block = [byte[]]::new($maxBytes)
    $random.NextBytes($block)

    for ($directory = 0; $directory -lt $directoryCount; $directory++) {
        New-Item -ItemType Directory -Force -Path (Join-Path $root ('d{0:D4}' -f $directory)) | Out-Null
    }

    for ($index = 0; $index -lt $fileCount; $index++) {
        $directory = $index % $directoryCount
        $size = $random.Next($minBytes, $maxBytes + 1)
        # Distinct content per file: a slice of the shared block, shifted by the index, so that no two files dedupe.
        $bytes = [byte[]]::new($size)
        [System.Array]::Copy($block, $index % ($maxBytes - $size + 1), $bytes, 0, $size)
        $bytes[0] = [byte]($index -band 0xFF)
        $bytes[[Math]::Min(1, $size - 1)] = [byte](($index -shr 8) -band 0xFF)
        $bytes[[Math]::Min(2, $size - 1)] = [byte](($index -shr 16) -band 0xFF)
        [System.IO.File]::WriteAllBytes((Join-Path $root ('d{0:D4}\f{1:D6}.bin' -f $directory, $index)), $bytes)
    }
}

$scratch = [System.IO.Path]::GetFullPath($ScratchDirectory)
Write-Host "Scratch directory: $scratch (it should be excluded from Windows Defender scanning)"
$fixtures = Join-Path $scratch 'fixtures'
$work = Join-Path $scratch 'work'
Remove-Tree $work
New-Item -ItemType Directory -Force -Path $work | Out-Null

$smallSource = Join-Path $fixtures 'small'
$largeSource = Join-Path $fixtures 'large'

if (-not (Test-Path -LiteralPath $smallSource) -or (Get-ChildItem -LiteralPath $smallSource -Recurse -File).Count -ne $SmallFileCount) {
    Write-Host "Generating the small fixture ($SmallFileCount files of 1 to 64 KB)..."
    New-Fixture $smallSource $SmallFileCount ([Math]::Max(1, [Math]::Min(100, $SmallFileCount))) 1KB 64KB 1
}
if (-not (Test-Path -LiteralPath $largeSource) -or (Get-ChildItem -LiteralPath $largeSource -Recurse -File).Count -ne $LargeFileCount) {
    Write-Host "Generating the large tree ($LargeFileCount files of 1 to 4 KB in 200 directories)..."
    New-Fixture $largeSource $LargeFileCount ([Math]::Max(1, [Math]::Min(200, $LargeFileCount))) 1KB 4KB 2
}

# ---------------------------------------------------------------- running

function New-Profile([string] $source) {
    $target = Join-Path $work 'target'
    $config = Join-Path $work 'profiles.yml'
    Remove-Tree $target
    @"
profiles:
  - name: bench
    target: $target
    sources:
      - path: $source
"@ | Set-Content -LiteralPath $config -Encoding utf8
    return $config
}

# One timed process run: returns wall-clock milliseconds, CPU milliseconds and the peak working set in bytes.
function Measure-Run([string[]] $arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($info)
    # Drain both streams so that a full pipe never stalls the process.
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $peak = 0L
    while (-not $process.WaitForExit(20)) {
        try { $process.Refresh(); $peak = [Math]::Max($peak, $process.PeakWorkingSet64) } catch { }
    }
    $process.WaitForExit()
    $watch.Stop()
    [void]$stdout.GetAwaiter().GetResult()
    $errorText = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "$Exe exited with $($process.ExitCode): $errorText" }
    return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds; PeakBytes = $peak }
}

# `backup --json` installs no progress observers: the engine and the process, nothing else.
function Get-BackupArguments([string] $config) { return @('backup', 'bench', '--config', $config, '--json') }

# Runs one sample. Scenario 2 makes its first backup untimed, and measures the second.
function Invoke-Sample([string] $source, [bool] $primeFirst) {
    $config = New-Profile $source
    if ($primeFirst) { [void](Measure-Run (Get-BackupArguments $config)) }
    return Measure-Run (Get-BackupArguments $config)
}

$scenarios = @(
    [pscustomobject]@{ Name = '1 first backup, 2,000 files'; Source = $smallSource; Prime = $false; Large = $false },
    [pscustomobject]@{ Name = '2 nothing changed'; Source = $smallSource; Prime = $true; Large = $false },
    [pscustomobject]@{ Name = '3 large tree first backup'; Source = $largeSource; Prime = $false; Large = $true }
)

# Reads every file once, so that the file cache is warm from the start.
foreach ($source in @($smallSource, $largeSource)) {
    Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object { [void][System.IO.File]::ReadAllBytes($_.FullName) }
}

function Get-Quantile([double[]] $sorted, [double] $q) {
    if ($sorted.Count -eq 1) { return $sorted[0] }
    $position = ($sorted.Count - 1) * $q
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    return $sorted[$lower] + ($sorted[$upper] - $sorted[$lower]) * ($position - $lower)
}

$samples = @{}
foreach ($scenario in $scenarios) {
    $samples[$scenario.Name] = [System.Collections.Generic.List[object]]::new()
    Write-Host "Scenario $($scenario.Name): $RunCount runs, the first discarded."
    for ($run = 0; $run -lt $RunCount; $run++) {
        $sample = Invoke-Sample $scenario.Source $scenario.Prime
        if ($run -gt 0) { $samples[$scenario.Name].Add($sample) }
        Write-Host ('  run {0,2}  {1,9:N0} ms wall  {2,9:N0} ms cpu  {3,7:N1} MB{4}' -f $run, $sample.Milliseconds, $sample.CpuMilliseconds, ($sample.PeakBytes / 1MB), $(if ($run -eq 0) { '  (warm-up)' } else { '' }))
    }
}

# ---------------------------------------------------------------- figures

$figures = foreach ($scenario in $scenarios) {
    $set = $samples[$scenario.Name]
    $times = [double[]]($set | ForEach-Object { $_.Milliseconds } | Sort-Object)
    $cpus = [double[]]($set | ForEach-Object { $_.CpuMilliseconds } | Sort-Object)
    $peaks = [double[]]($set | ForEach-Object { $_.PeakBytes } | Sort-Object)
    $cpuMedian = Get-Quantile $cpus 0.5
    [pscustomobject]@{
        Scenario = $scenario.Name
        Large = $scenario.Large
        Samples = $times.Count
        CpuMedianMs = $cpuMedian
        CpuBestMs = $cpus[0]
        CpuIqrPercent = if ($cpuMedian -gt 0) { 100 * ((Get-Quantile $cpus 0.75) - (Get-Quantile $cpus 0.25)) / $cpuMedian } else { 0 }
        WallMedianMs = Get-Quantile $times 0.5
        WallBestMs = $times[0]
        PeakMB = (Get-Quantile $peaks 0.5) / 1MB
    }
}
$exeBytes = (Get-Item -LiteralPath $Exe).Length

# The machine and the antimalware state matter to how a time is read.
$cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$os = (Get-CimInstance Win32_OperatingSystem)
$defender = try {
    $status = Get-MpComputerStatus
    "real-time protection $($status.RealTimeProtectionEnabled), antimalware service $($status.AMServiceEnabled)"
} catch { 'not readable (Get-MpComputerStatus failed)' }
$machine = '{0}; {1}; {2:N0} GB RAM' -f $cpuName, $os.Caption, ($os.TotalVisibleMemorySize / 1MB)

if ($Record) {
    New-Item -ItemType Directory -Force -Path $BaselineDirectory | Out-Null
    [pscustomobject]@{
        Date = (Get-Date).ToString('u')
        Machine = $machine
        Antimalware = $defender
        RunCount = $RunCount
        SmallFileCount = $SmallFileCount
        LargeFileCount = $LargeFileCount
        ExecutableBytes = $exeBytes
        Scenarios = $figures
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $baselineFile -Encoding utf8
    Write-Host "Recorded $baselineFile"
    foreach ($row in $figures) {
        Write-Host ('  {0}: cpu median {1:N0} ms (IQR {2:N1}%), wall median {3:N0} ms, peak {4:N1} MB' -f $row.Scenario, $row.CpuMedianMs, $row.CpuIqrPercent, $row.WallMedianMs, $row.PeakMB)
    }
    Write-Host ('  executable: {0:N0} bytes' -f $exeBytes)
    exit 0
}

# ---------------------------------------------------------------- verdict

$baseline = Get-Content -LiteralPath $baselineFile -Raw | ConvertFrom-Json
if ($baseline.SmallFileCount -ne $SmallFileCount -or $baseline.LargeFileCount -ne $LargeFileCount) {
    throw 'The baseline was recorded with other fixture sizes; run with the same -SmallFileCount and -LargeFileCount.'
}

$verdicts = [System.Collections.Generic.List[string]]::new()
$pass = $true
$inconclusive = $false
foreach ($row in $figures) {
    $reference = $baseline.Scenarios | Where-Object { $_.Scenario -eq $row.Scenario }
    if (-not $reference) { throw "The baseline has no scenario '$($row.Scenario)'." }

    $cpuMedianRatio = $row.CpuMedianMs / $reference.CpuMedianMs
    $cpuBestRatio = $row.CpuBestMs / $reference.CpuBestMs
    $cpuOk = ($cpuMedianRatio -le 1.10) -and ($cpuBestRatio -le 1.10)
    $noisy = $row.CpuIqrPercent -gt 5
    $outcome = if ($cpuOk) { 'pass' } elseif ($noisy) { 'INCONCLUSIVE (IQR above 5%; run again on a quiet machine)' } else { 'FAIL' }
    if (-not $cpuOk -and $noisy) { $inconclusive = $true } elseif (-not $cpuOk) { $pass = $false }
    $verdicts.Add(('cpu    {0}: run/baseline median {1:N3}, best {2:N3} (limit 1.10 for both; IQR {3:N1}%): {4}' -f $row.Scenario, $cpuMedianRatio, $cpuBestRatio, $row.CpuIqrPercent, $outcome))

    $wallMedianRatio = $row.WallMedianMs / $reference.WallMedianMs
    $wallBestRatio = $row.WallBestMs / $reference.WallBestMs
    $investigate = ($wallMedianRatio -gt 1.15) -and ($wallBestRatio -gt 1.15)
    $verdicts.Add(('wall   {0}: run/baseline median {1:N3}, best {2:N3} (advisory; both above 1.15 means investigate): {3}' -f $row.Scenario, $wallMedianRatio, $wallBestRatio, $(if ($investigate) { 'INVESTIGATE' } else { 'ok' })))

    $memoryRatio = $row.PeakMB / $reference.PeakMB
    if ($row.Large) {
        $memoryOk = $memoryRatio -le 1.10
        if (-not $memoryOk) { $pass = $false }
        $verdicts.Add(('memory {0}: run/baseline peak working set {1:N3} (limit 1.10): {2}' -f $row.Scenario, $memoryRatio, $(if ($memoryOk) { 'pass' } else { 'FAIL' })))
    } else {
        $verdicts.Add(('memory {0}: run/baseline peak working set {1:N3} (recorded)' -f $row.Scenario, $memoryRatio))
    }
}
$sizeRatio = $exeBytes / $baseline.ExecutableBytes
$sizeOk = $sizeRatio -le 1.05
if (-not $sizeOk) { $pass = $false }
$verdicts.Add(('size: run/baseline executable {0:N3} (limit 1.05): {1}' -f $sizeRatio, $(if ($sizeOk) { 'pass' } else { 'FAIL' })))

$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine('# Release check')
[void]$report.AppendLine()
[void]$report.AppendLine(('Date: {0:u}. Runs per scenario: {1}, the first discarded (samples: {2}).' -f (Get-Date), $RunCount, ($RunCount - 1)))
[void]$report.AppendLine("Machine: $machine; scratch directory ``$scratch``.")
[void]$report.AppendLine("Antimalware: $defender.")
[void]$report.AppendLine("Baseline: ``$baselineFile`` (recorded $($baseline.Date) on $($baseline.Machine)).")
[void]$report.AppendLine()
[void]$report.AppendLine('| Scenario | N | CPU median ms | CPU best ms | CPU IQR % | Wall median ms | Wall best ms | Peak working set MB |')
[void]$report.AppendLine('|---|---:|---:|---:|---:|---:|---:|---:|')
foreach ($row in $figures) {
    [void]$report.AppendLine(('| {0} | {1} | {2:N0} | {3:N0} | {4:N1} | {5:N0} | {6:N0} | {7:N1} |' -f $row.Scenario, $row.Samples, $row.CpuMedianMs, $row.CpuBestMs, $row.CpuIqrPercent, $row.WallMedianMs, $row.WallBestMs, $row.PeakMB))
}
[void]$report.AppendLine()
[void]$report.AppendLine(('Executable: {0:N0} bytes.' -f $exeBytes))
[void]$report.AppendLine()
[void]$report.AppendLine('Allocations are held separately by the BenchmarkDotNet baseline (budget 5%).')
[void]$report.AppendLine()
[void]$report.AppendLine('Verdict by rule:')
[void]$report.AppendLine()
foreach ($line in $verdicts) { [void]$report.AppendLine("- $line") }
[void]$report.AppendLine()
$overall = if (-not $pass) { 'FAIL (see the rules above)' } elseif ($inconclusive) { 'INCONCLUSIVE (a noisy scenario)' } else { 'PASS' }
[void]$report.AppendLine("**Overall: $overall**")

Write-Host ''
Write-Host $report.ToString()

if ($OutputDirectory) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $report.ToString() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-check.md') -Encoding utf8
}

if (-not $pass) { exit 1 }
