#requires -Version 7.0
<#
.SYNOPSIS
Collect existing local Hyper-V and disk performance counters to local CSV/JSON.
.EXAMPLE
pwsh -File ./scripts/ci/Watch-HyperVPerformance.ps1 -SampleIntervalSeconds 5 -DurationSeconds 7200
.NOTES
Run separately on the Windows VM hosting runners and, optionally, the physical
Hyper-V host. No remoting, metering, services, permissions or VM settings change.
Counter/instance paths can identify infrastructure: keep these reports local.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 3600)][int]$SampleIntervalSeconds = 5,
    [ValidateRange(1, 86400)][int]$DurationSeconds = 7200,
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA ("AsapDiagnostics/hyperv-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')))
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    throw 'This optional collector requires Windows and PowerShell 7.'
}
$directory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$csv = Join-Path $directory 'samples.csv'
$reportPath = Join-Path $directory 'report.json'
if ((Test-Path -LiteralPath $csv) -or (Test-Path -LiteralPath $reportPath)) {
    throw 'Choose a new output directory to preserve existing diagnostics.'
}
$report = [ordered]@{
    schema_version = 1
    started_utc = [DateTimeOffset]::UtcNow.ToString('O')
    finished_utc = $null
    sample_interval_seconds = $SampleIntervalSeconds
    maximum_duration_seconds = $DurationSeconds
    status = 'discovering'
    supported_counter_paths = @()
    unavailable_counter_sets = @()
    sample_sets = 0
    valid_values = 0
    invalid_values = 0
    read_error_count = 0
    limitations = @(
        'Local English counter names are discovered before use; localized, absent or inaccessible sets are unavailable.'
        'Counters and instances depend on Windows/Hyper-V version. First rate samples and disappeared instances may be invalid.'
        'Cooked values keep native counter units: PhysicalDisk Avg. Disk sec/* are seconds; Hyper-V latency/wait units depend on counter definition.'
        'No samples are uploaded. Counter paths/instances may identify VMs, disks and the machine.'
        'UTC clocks must be synchronized with the CI guest. Nested-host and outer-host runs measure different layers.'
    )
}
$specifications = [ordered]@{
    'Hyper-V Hypervisor Virtual Processor' = '\\(% (Guest|Hypervisor|Total) Run Time|CPU (Wait|Contention) Time Per Dispatch|Logical Processor Dispatches/sec)$'
    'Hyper-V Hypervisor Logical Processor' = '\\(% (Guest|Hypervisor|Total) Run Time|Context Switches/sec)$'
    'Hyper-V Virtual Storage Device' = '\\((Read|Write) (Bytes/sec|Operations/sec|Latency)|.*(Latency|Queue Length))$'
    'PhysicalDisk' = '\\(Avg\. Disk sec/(Read|Write)|Current Disk Queue Length|Avg\. Disk Queue Length|Disk (Read|Write) Bytes/sec|Disk (Reads|Writes)/sec)$'
    'Memory' = '\\(Available MBytes|Pages/sec)$'
    'System' = '\\Processor Queue Length$'
}

function Save-LocalReport {
    $temporary = $reportPath + '.tmp'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $reportPath -Force
}

try {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($name in $specifications.Keys) {
        $discoveryErrors = @()
        $sets = @(Get-Counter -ListSet $name -ErrorAction SilentlyContinue -ErrorVariable discoveryErrors)
        $selected = @($sets | ForEach-Object { $_.Paths } | Where-Object { $_ -match $specifications[$name] })
        if ($selected.Count -eq 0) {
            $report.unavailable_counter_sets += $name
        }
        foreach ($path in $selected) {
            $paths.Add($path)
        }
    }
    $report.supported_counter_paths = @($paths | Sort-Object -Unique)
    if ($report.supported_counter_paths.Count -eq 0) {
        $report.status = 'unavailable'
        Write-Warning 'No requested local counters are accessible. See the local report.'
        return
    }
    $report.status = 'sampling'
    Save-LocalReport
    Write-Host "Monitoring local counters. Stop with Ctrl+C. Reports: $directory"
    # One Get-Counter session preserves rate-counter baselines. Do not restart it
    # each interval. The finite sample count bounds collection without a service.
    $maximumSamples = [Math]::Max(1, [int][Math]::Floor($DurationSeconds / $SampleIntervalSeconds))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $counterErrors = @()
    Get-Counter -Counter $report.supported_counter_paths -SampleInterval $SampleIntervalSeconds -MaxSamples $maximumSamples `
        -ErrorAction SilentlyContinue -ErrorVariable counterErrors | ForEach-Object {
            if ($clock.Elapsed.TotalSeconds -gt $DurationSeconds) {
                throw [TimeoutException]::new('Requested sampling duration reached.')
            }
            $sample = $_
            $rows = foreach ($counter in $sample.CounterSamples) {
                $valid = $counter.Status -eq 0 -and [double]::IsFinite($counter.CookedValue)
                if ($valid) { $report.valid_values++ } else { $report.invalid_values++ }
                [pscustomobject][ordered]@{
                    timestamp_utc = $sample.Timestamp.ToUniversalTime().ToString('O')
                    counter_timestamp_utc = $counter.Timestamp.ToUniversalTime().ToString('O')
                    counter_path = $counter.Path
                    instance = $counter.InstanceName
                    counter_type = [string]$counter.CounterType
                    cooked_value = if ($valid) { $counter.CookedValue } else { $null }
                    status = $counter.Status
                }
            }
            $rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Append -Encoding utf8NoBOM
            $report.sample_sets++
            $report.read_error_count = $counterErrors.Count
            Save-LocalReport
        }
    $report.status = if ($report.valid_values -gt 0) { 'completed' } else { 'unavailable' }
}
catch [TimeoutException] {
    $report.status = 'completed'
}
catch [OperationCanceledException] {
    $report.status = 'interrupted'
}
catch {
    $report.status = 'failed'
    $report.error_type = $_.Exception.GetType().Name
    Write-Warning 'Counter collection stopped; completed local evidence is retained.'
}
finally {
    if ($report.status -eq 'sampling') { $report.status = 'interrupted' }
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-LocalReport
    Write-Host "Local report: $reportPath"
}
