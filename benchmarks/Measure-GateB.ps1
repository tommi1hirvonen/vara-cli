<#
.SYNOPSIS
    Gate B of the F# migration: measures the Native AOT F# vara.exe against the Native AOT C# one.

.DESCRIPTION
    Implements the method of openspec/changes/fsharp-backup-presentation/design.md (D8).

    Scenarios (all on the real filesystem under -ScratchDirectory):
      1. first backup of a fixture of 2,000 files of 1 to 64 KB;
      2. a second backup of the same source with nothing changed;
      3. first backup of a large tree of 4,000 files of 1 to 4 KB in 200 directories.

    Each run starts from a fresh target. The builds alternate (C#, F#, F#, C#, ...) so that drift hits both equally,
    and the first run of each scenario is a warm-up that is discarded. Per sample the script records wall-clock time
    around the process and the peak working set; it also records the size of each executable.

    Rules (D8):
      - time: the process's CPU time (user plus kernel), which leaves out most waiting on the disk. F# passes a
        scenario when its median CPU time is at most 1.10 times that of C# AND its fastest run is at most 1.10 times
        the fastest C# run (the fastest run is the least affected by noise). Wall-clock time is reported and is
        advisory: both its median and its best-of-N ratio above 1.15 is flagged "investigate", never failed;
      - memory: peak working set of F# at most 1.10 times that of C# in the large-tree scenario (recorded for the rest);
      - executable size: at most 1.25 times the C# executable;
      - allocations are held by LanguageComparisonBenchmarks (5% budget), cited in the report, not remeasured here.
    A noisy interquartile range is printed but does not decide the verdict. The script prints a table and a verdict per
    rule, and writes the same to -OutputDirectory when given.

.PARAMETER CSharpExe
    The Native AOT C# vara.exe (dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cs-cli).

.PARAMETER FSharpExe
    The Native AOT F# vara.exe (dotnet publish src/fsharp/Vara.Cli -c Release -r win-x64 -o artifacts/fs-cli).

.PARAMETER ScratchDirectory
    Where fixtures and targets are created. Use a directory on the system drive; it is deleted and recreated.

.PARAMETER RunCount
    Runs per scenario per build, the first being the discarded warm-up. Default 5.

.PARAMETER SmallFileCount
    Files of the small fixture (scenarios 1 and 2). Default 2000.

.PARAMETER LargeFileCount
    Files of the large tree (scenario 3). Default 4000, in 200 directories.

.PARAMETER Output
    'json' (default) runs `backup --json`, which installs no progress observers in either build: the engine and the
    process, nothing else. 'plain' runs with redirected output, which appends progress lines like a script would see.

.PARAMETER OutputDirectory
    When given, writes gate-b.md (the table, machine and verdict) there.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CSharpExe,
    [Parameter(Mandatory)] [string] $FSharpExe,
    [Parameter(Mandatory)] [string] $ScratchDirectory,
    [int] $RunCount = 5,
    [int] $SmallFileCount = 2000,
    [int] $LargeFileCount = 4000,
    [ValidateSet('json', 'plain')] [string] $Output = 'json',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Numbers in the report use a point and no group separator, whatever the machine's culture.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

foreach ($exe in @($CSharpExe, $FSharpExe)) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
}
if ($RunCount -lt 3) { throw 'RunCount must be at least 3: one warm-up and two samples.' }

$CSharpExe = (Resolve-Path -LiteralPath $CSharpExe).Path
$FSharpExe = (Resolve-Path -LiteralPath $FSharpExe).Path
$builds = [ordered]@{ 'C#' = $CSharpExe; 'F#' = $FSharpExe }

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

function New-Profile([string] $name, [string] $source) {
    $label = if ($name -eq 'C#') { 'cs' } else { 'fs' }
    $target = Join-Path $work "target-$label"
    $config = Join-Path $work "profiles-$label.yml"
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
function Measure-Run([string] $exe, [string[]] $arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($exe)
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
    if ($process.ExitCode -ne 0) { throw "$exe exited with $($process.ExitCode): $errorText" }
    return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds; PeakBytes = $peak }
}

$backupArguments = { param($config) @('backup', 'bench', '--config', $config) + $(if ($Output -eq 'json') { @('--json') } else { @() }) }

# Runs one sample for a build. `prepare` runs untimed and returns the config; scenario 2 makes its first backup there.
function Invoke-Sample([string] $build, [string] $source, [bool] $primeFirst) {
    $config = New-Profile $build $source
    $exe = $builds[$build]
    if ($primeFirst) { [void](Measure-Run $exe (& $backupArguments $config)) }
    return Measure-Run $exe (& $backupArguments $config)
}

$scenarios = @(
    [pscustomobject]@{ Name = '1 first backup, 2,000 files'; Source = $smallSource; Prime = $false; Large = $false },
    [pscustomobject]@{ Name = '2 nothing changed'; Source = $smallSource; Prime = $true; Large = $false },
    [pscustomobject]@{ Name = '3 large tree first backup'; Source = $largeSource; Prime = $false; Large = $true }
)

# Reads one file through each scenario's source once, so that the file cache is warm for both builds from the start.
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
    foreach ($build in $builds.Keys) { $samples["$($scenario.Name)|$build"] = [System.Collections.Generic.List[object]]::new() }

    Write-Host "Scenario $($scenario.Name): $RunCount runs per build, the first discarded."
    for ($run = 0; $run -lt $RunCount; $run++) {
        $order = if ($run % 2 -eq 0) { @('C#', 'F#') } else { @('F#', 'C#') }
        foreach ($build in $order) {
            $sample = Invoke-Sample $build $scenario.Source $scenario.Prime
            if ($run -gt 0) { $samples["$($scenario.Name)|$build"].Add($sample) }
            Write-Host ('  run {0,2} {1}  {2,9:N0} ms wall  {3,9:N0} ms cpu  {4,7:N1} MB{5}' -f $run, $build, $sample.Milliseconds, $sample.CpuMilliseconds, ($sample.PeakBytes / 1MB), $(if ($run -eq 0) { '  (warm-up)' } else { '' }))
        }
    }
}

# ---------------------------------------------------------------- report

$rows = foreach ($scenario in $scenarios) {
    foreach ($build in $builds.Keys) {
        $set = $samples["$($scenario.Name)|$build"]
        $times = [double[]]($set | ForEach-Object { $_.Milliseconds } | Sort-Object)
        $cpus = [double[]]($set | ForEach-Object { $_.CpuMilliseconds } | Sort-Object)
        $peaks = [double[]]($set | ForEach-Object { $_.PeakBytes } | Sort-Object)
        $median = Get-Quantile $times 0.5
        $iqr = (Get-Quantile $times 0.75) - (Get-Quantile $times 0.25)
        $cpuMedian = Get-Quantile $cpus 0.5
        [pscustomobject]@{
            Scenario = $scenario.Name; Build = $build; N = $times.Count
            CpuMedianMs = $cpuMedian; CpuMinMs = $cpus[0]
            CpuIqrPercent = if ($cpuMedian -gt 0) { 100 * ((Get-Quantile $cpus 0.75) - (Get-Quantile $cpus 0.25)) / $cpuMedian } else { 0 }
            WallMedianMs = $median; WallMinMs = $times[0]; WallIqrPercent = if ($median -gt 0) { 100 * $iqr / $median } else { 0 }
            PeakMB = (Get-Quantile $peaks 0.5) / 1MB; Large = $scenario.Large
        }
    }
}

$sizes = foreach ($build in $builds.Keys) { [pscustomobject]@{ Build = $build; Bytes = (Get-Item -LiteralPath $builds[$build]).Length } }
$sizeRatio = ($sizes | Where-Object Build -eq 'F#').Bytes / ($sizes | Where-Object Build -eq 'C#').Bytes

$verdicts = [System.Collections.Generic.List[string]]::new()
$pass = $true
foreach ($scenario in $scenarios) {
    $cs = $rows | Where-Object { $_.Scenario -eq $scenario.Name -and $_.Build -eq 'C#' }
    $fs = $rows | Where-Object { $_.Scenario -eq $scenario.Name -and $_.Build -eq 'F#' }

    $cpuMedianRatio = $fs.CpuMedianMs / $cs.CpuMedianMs
    $cpuMinRatio = $fs.CpuMinMs / $cs.CpuMinMs
    $cpuOk = ($cpuMedianRatio -le 1.10) -and ($cpuMinRatio -le 1.10)
    if (-not $cpuOk) { $pass = $false }
    $verdicts.Add(('cpu    {0}: F#/C# median {1:N3}, best {2:N3} (limit 1.10 for both): {3}' -f $scenario.Name, $cpuMedianRatio, $cpuMinRatio, $(if ($cpuOk) { 'pass' } else { 'FAIL' })))

    $wallMedianRatio = $fs.WallMedianMs / $cs.WallMedianMs
    $wallMinRatio = $fs.WallMinMs / $cs.WallMinMs
    $investigate = ($wallMedianRatio -gt 1.15) -and ($wallMinRatio -gt 1.15)
    $verdicts.Add(('wall   {0}: F#/C# median {1:N3}, best {2:N3} (advisory; both above 1.15 means investigate): {3}' -f $scenario.Name, $wallMedianRatio, $wallMinRatio, $(if ($investigate) { 'INVESTIGATE' } else { 'ok' })))

    $memoryRatio = $fs.PeakMB / $cs.PeakMB
    if ($scenario.Large) {
        $memoryOk = $memoryRatio -le 1.10
        if (-not $memoryOk) { $pass = $false }
        $verdicts.Add(('memory {0}: F#/C# peak working set {1:N3} (limit 1.10): {2}' -f $scenario.Name, $memoryRatio, $(if ($memoryOk) { 'pass' } else { 'FAIL' })))
    } else {
        $verdicts.Add(('memory {0}: F#/C# peak working set {1:N3} (recorded)' -f $scenario.Name, $memoryRatio))
    }
}
$sizeOk = $sizeRatio -le 1.25
if (-not $sizeOk) { $pass = $false }
$verdicts.Add(('size: F#/C# executable {0:N3} (limit 1.25): {1}' -f $sizeRatio, $(if ($sizeOk) { 'pass' } else { 'FAIL' })))

# The machine and the antimalware state matter to how a time is read.
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$os = (Get-CimInstance Win32_OperatingSystem)
$defender = try {
    $status = Get-MpComputerStatus
    "real-time protection $($status.RealTimeProtectionEnabled), antimalware service $($status.AMServiceEnabled)"
} catch { 'not readable (Get-MpComputerStatus failed)' }

$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine('# Gate B')
[void]$report.AppendLine()
[void]$report.AppendLine(('Date: {0:u}. Output mode: `{1}`. Runs per scenario per build: {2}, the first discarded (samples: {3}).' -f (Get-Date), $Output, $RunCount, ($RunCount - 1)))
[void]$report.AppendLine(('Machine: {0}; {1}; {2:N0} GB RAM; scratch directory `{3}`.' -f $cpu, $os.Caption, ($os.TotalVisibleMemorySize / 1MB), $scratch))
[void]$report.AppendLine("Antimalware: $defender.")
[void]$report.AppendLine()
[void]$report.AppendLine('| Scenario | Build | N | CPU median ms | CPU best ms | CPU IQR % | Wall median ms | Wall best ms | Wall IQR % | Peak working set MB |')
[void]$report.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($row in $rows) {
    [void]$report.AppendLine(('| {0} | {1} | {2} | {3:N0} | {4:N0} | {5:N1} | {6:N0} | {7:N0} | {8:N1} | {9:N1} |' -f $row.Scenario, $row.Build, $row.N, $row.CpuMedianMs, $row.CpuMinMs, $row.CpuIqrPercent, $row.WallMedianMs, $row.WallMinMs, $row.WallIqrPercent, $row.PeakMB))
}
[void]$report.AppendLine()
[void]$report.AppendLine('| Executable | Bytes |')
[void]$report.AppendLine('|---|---:|')
foreach ($size in $sizes) { [void]$report.AppendLine(('| {0} | {1:N0} |' -f $size.Build, $size.Bytes)) }
[void]$report.AppendLine()
[void]$report.AppendLine('Allocations are held separately by `LanguageComparisonBenchmarks` (budget 5% against C#); see the language-comparison baseline.')
[void]$report.AppendLine()
[void]$report.AppendLine('Verdict by rule:')
[void]$report.AppendLine()
foreach ($line in $verdicts) { [void]$report.AppendLine("- $line") }
[void]$report.AppendLine()
[void]$report.AppendLine("**Overall: $(if ($pass) { 'PASS' } else { 'FAIL (see the rules above)' })**")

Write-Host ''
Write-Host $report.ToString()

if ($OutputDirectory) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $report.ToString() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'gate-b.md') -Encoding utf8
}

if (-not $pass) { exit 1 }
