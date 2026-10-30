#requires -Version 7.0
# Focused mock-counter checks; does not query Hyper-V or require Pester/admin.
$ErrorActionPreference = 'Stop'
$collector = Join-Path $PSScriptRoot 'Watch-HyperVPerformance.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('asap-hyperv-check-' + [guid]::NewGuid().ToString('N'))
$global:AsapHyperVDiagnosticTestState = @{ Mode = 'available'; SampleCalls = 0 }

function Get-Counter {
    [CmdletBinding()]
    param([string]$ListSet, [string[]]$Counter, [int]$SampleInterval, [int]$MaxSamples)
    if ($ListSet) {
        if ($global:AsapHyperVDiagnosticTestState.Mode -eq 'unavailable') { return }
        switch ($ListSet) {
            'Hyper-V Hypervisor Virtual Processor' {
                [pscustomobject]@{ Paths = @('\Hyper-V Hypervisor Virtual Processor(*)\CPU Wait Time Per Dispatch', '\Hyper-V Hypervisor Virtual Processor(*)\% Total Run Time') }
            }
            'PhysicalDisk' {
                [pscustomobject]@{ Paths = @('\PhysicalDisk(*)\Avg. Disk sec/Read', '\PhysicalDisk(*)\Current Disk Queue Length', '\PhysicalDisk(*)\Unrequested Counter') }
            }
        }
        return
    }
    $global:AsapHyperVDiagnosticTestState.SampleCalls++
    if ($Counter -contains '\PhysicalDisk(*)\Unrequested Counter') { throw 'Counter allow-list failed.' }
    if ($Counter.Count -ne 4) { throw 'Counter discovery failed.' }
    $timestamp = [datetime]::UtcNow
    [pscustomobject]@{ Timestamp = $timestamp; CounterSamples = @(
        [pscustomobject]@{ Timestamp = $timestamp; Path = '\local\PhysicalDisk(0)\Avg. Disk sec/Read'; InstanceName = '0'; CounterType = 'AverageTimer32'; CookedValue = .002; Status = 0 },
        [pscustomobject]@{ Timestamp = $timestamp; Path = '\local\PhysicalDisk(1)\Avg. Disk sec/Read'; InstanceName = '1'; CounterType = 'AverageTimer32'; CookedValue = [double]::NaN; Status = 1 }
    ) }
    if ($global:AsapHyperVDiagnosticTestState.Mode -eq 'interrupted') { throw [OperationCanceledException]::new() }
}

function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    foreach ($mode in @('available', 'unavailable', 'interrupted')) {
        $global:AsapHyperVDiagnosticTestState.Mode = $mode
        $global:AsapHyperVDiagnosticTestState.SampleCalls = 0
        $output = Join-Path $root $mode
        & $collector -SampleIntervalSeconds 1 -DurationSeconds 2 -OutputDirectory $output
        $rawReport = Get-Content -LiteralPath (Join-Path $output 'report.json') -Raw
        $report = $rawReport | ConvertFrom-Json
        Assert-Check ($rawReport -match '"started_utc": "[^"]+[+]00:00"' -and $rawReport -match '"finished_utc": "[^"]+[+]00:00"') 'UTC timestamps missing.'
        if ($mode -eq 'unavailable') {
            Assert-Check ($report.status -eq 'unavailable' -and $global:AsapHyperVDiagnosticTestState.SampleCalls -eq 0) 'Unavailable counters were sampled.'
            continue
        }
        $expected = if ($mode -eq 'interrupted') { 'interrupted' } else { 'completed' }
        Assert-Check ($report.status -eq $expected) "Unexpected status for $mode."
        $csv = @(Import-Csv -LiteralPath (Join-Path $output 'samples.csv'))
        Assert-Check ($csv.Count -eq 2 -and $report.sample_sets -eq 1) 'Partial sample not retained.'
        Assert-Check ($report.valid_values -eq 1 -and $report.invalid_values -eq 1) 'Invalid values became measurements.'
        Assert-Check ([string]::IsNullOrEmpty($csv[1].cooked_value)) 'Invalid cooked value was exported.'
        Assert-Check ($report.unavailable_counter_sets.Count -eq 4) 'Missing counter sets were not reported.'
    }
    Write-Host 'PASS: discovery, unavailable counters, invalid metrics, UTC, interruption and local evidence retention.'
}
finally {
    Remove-Variable -Name AsapHyperVDiagnosticTestState -Scope Global
    # Only delete the exact temporary test directory verified under the OS temp root.
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
