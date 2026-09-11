#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Collects per-key execution-time and performance statistics from MqttBenchmark campaign
    artifacts and writes a self-contained, dated run report (run-stats.md + key-timing.json +
    aggregates.json + estimate.json) into a report folder.
.DESCRIPTION
    Portable by design: all paths are resolved relative to this script's own location
    ($PSScriptRoot) or supplied via parameters, so it can live in the repo and run on any
    checkout / CI without edits. Expected campaign layout:
        <RepoRoot>/campaigns/<campaignPrefix><broker>/attempts/<key>/attempt-*/result.json
    Attempt classification:
        clock-alignment "unready" -> WARNING (env/time-sync; throughput valid, latency unreliable)
        NIC sampling-gap / wrap   -> hard reject (link saturation)
        key failed ONLY on clock  -> reported as "warned" (not "failed")
.PARAMETER RepoRoot        Repository root (default: auto-detected near this script).
.PARAMETER CampaignsRoot   Campaigns root (default: <RepoRoot>/campaigns).
.PARAMETER OutFolder       Report output folder (default: <RepoRoot>/run-report-<yyyyMMdd>).
.PARAMETER CampaignPrefix  Broker campaign dir prefix for auto-discovery (default: "full-g100-").
.PARAMETER TotalPlanned    Total planned keys across brokers (default: 144).
.PARAMETER KeysPerBroker   Planned keys per broker for per-broker progress bars (default: 72).
.PARAMETER RunStartLocal   Run start (local) for elapsed/ETA (default: earliest artifact).
.EXAMPLE
    pwsh ./scripts/ExtractRunStats.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$CampaignsRoot,
    [string]$OutFolder,
    [string]$CampaignPrefix = 'full-g100-',
    [int]$TotalPlanned = 144,
    [int]$KeysPerBroker = 72,
    [string]$RunStartLocal = ''
)
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------- Resolve relative paths (portable) ----------
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) {
    $parent = Split-Path -Parent $PSScriptRoot
    if (Test-Path (Join-Path $parent 'campaigns')) { $RepoRoot = $parent }
    elseif (Test-Path (Join-Path $PSScriptRoot 'campaigns')) { $RepoRoot = $PSScriptRoot }
    else { $RepoRoot = $parent }
}
if (-not $CampaignsRoot) { $CampaignsRoot = Join-Path $RepoRoot 'campaigns' }
if (-not $OutFolder) { $OutFolder = Join-Path $RepoRoot ('run-report-' + (Get-Date -Format 'yyyyMMdd')) }
New-Item -ItemType Directory -Force -Path $OutFolder | Out-Null
if (-not (Test-Path $CampaignsRoot)) { throw "Campaigns root not found: '$CampaignsRoot'. Pass -CampaignsRoot." }

# ---------- Discover broker campaigns ----------
$brokerCampaigns = @(Get-ChildItem -Path $CampaignsRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like ($CampaignPrefix + '*') } | Sort-Object Name)
if ($brokerCampaigns.Count -eq 0) { throw "No broker campaigns matched '$CampaignPrefix*' under '$CampaignsRoot'." }
# ---------- Helpers ----------
function StatMinAvgMax($coll) {
    $c = @($coll | Where-Object { $null -ne $_ })
    if ($c.Count -eq 0) { return [ordered]@{ min = $null; avg = $null; max = $null } }
    $m = $c | Measure-Object -Minimum -Maximum -Average
    [ordered]@{ min = [math]::Round($m.Minimum, 2); avg = [math]::Round($m.Average, 2); max = [math]::Round($m.Maximum, 2) }
}
function Progress-Bar([int]$done, [int]$total, [int]$width = 40) {
    if ($total -le 0) { return '[' + (' ' * $width) + '] 0/0  0.0%' }
    $frac = [Math]::Min(1.0, [Math]::Max(0.0, [double]($done / $total)))
    $filled = [int][Math]::Round($frac * $width)
    if ($filled -lt 0) { $filled = 0 }
    if ($filled -gt $width) { $filled = $width }
    return ('[' + ('#' * $filled) + ('-' * ($width - $filled)) + '] ' + $done + '/' + $total + '  ' + [math]::Round($frac * 100, 1) + '%')
}
function Classify-Error([string]$s) {
    if (-not $s) { return 'ok' }
    if ($s -match 'clock alignment is unready|Stand controller clock failed|clock-alignment') { return 'clock' }
    if ($s -match 'sampling gap|counter reset/wrap|Load-generator guard') { return 'nic' }
    return 'other'
}

# ---------- Collect per-key rows ----------
$rows = @()
$globalEarliest = $null
foreach ($bc in $brokerCampaigns) {
    $broker = $bc.Name.Substring($CampaignPrefix.Length)
    $attDir = Join-Path $bc.FullName 'attempts'
    if (-not (Test-Path $attDir)) { continue }
    foreach ($k in (Get-ChildItem $attDir -Directory | Sort-Object Name)) {
        $attDirs = @(Get-ChildItem -Path $k.FullName -Directory | Sort-Object Name)
        $files = @(Get-ChildItem $k.FullName -Recurse -File)
        if ($files.Count -eq 0) { continue }
        $keyStart = (@($files | Sort-Object LastWriteTime)[0]).LastWriteTime
        $keyEnd = (@($files | Sort-Object LastWriteTime)[-1]).LastWriteTime
        $keyDur = [math]::Round(($keyEnd - $keyStart).TotalSeconds, 1)
        if ($null -eq $globalEarliest -or $keyStart -lt $globalEarliest) { $globalEarliest = $keyStart }
        $succ = $null; $failReasons = @(); $running = $false; $clockWarns = 0; $nicRejects = 0; $otherFails = 0
        foreach ($a in $attDirs) {
            $r = Join-Path $a.FullName 'result.json'
            if (Test-Path $r) {
                $j = Get-Content $r -Raw | ConvertFrom-Json
                if ($j.status -eq 'success') { $succ = $j }
                else {
                    $failReasons += [string]$j.failure
                    $cls = Classify-Error ([string]$j.failure)
                    if ($cls -eq 'clock') { $clockWarns++ } elseif ($cls -eq 'nic') { $nicRejects++ } else { $otherFails++ }
                }
            } else { $running = $true }
        }
        $status = if ($succ) { 'success' } elseif ($running) { 'in-progress' } else { 'failed' }
        $displayStatus = $status
        if ($status -eq 'failed' -and $clockWarns -gt 0 -and $nicRejects -eq 0 -and $otherFails -eq 0) { $displayStatus = 'warned' }
        $o = if ($succ) { $succ.observation } else { $null }
        $rows += [ordered]@{
            broker = $broker; key = $k.Name; attempts = $attDirs.Count
            status = $status; displayStatus = $displayStatus
            startedLocal = $keyStart.ToString('HH:mm:ss'); endedLocal = $keyEnd.ToString('HH:mm:ss')
            keyDurationSeconds = $keyDur
            attemptedRps = if ($o) { [math]::Round($o.measurement.attemptedRps, 1) } else { $null }
            completedRps = if ($o) { [math]::Round($o.measurement.completedRps, 1) } else { $null }
            p50Ms = if ($o) { [math]::Round($o.measurement.publishLatencyMs.p50Ms, 2) } else { $null }
            p95Ms = if ($o) { [math]::Round($o.measurement.publishLatencyMs.p95Ms, 2) } else { $null }
            p99Ms = if ($o) { [math]::Round($o.measurement.publishLatencyMs.p99Ms, 2) } else { $null }
            maxMs = if ($o) { [math]::Round($o.measurement.publishLatencyMs.maxMs, 2) } else { $null }
            deliveryLossRate = if ($o) { [math]::Round($o.measurement.deliveryLossRate, 4) } else { $null }
            failureReasons = $failReasons; clockWarns = $clockWarns; nicRejects = $nicRejects; otherFails = $otherFails
        }
    }
}
if ($rows.Count -eq 0) { throw "No keys found under '$CampaignsRoot' (prefix '$CampaignPrefix')." }
# ---------- Run start + per-broker total ----------
if ($RunStartLocal) { $runStart = [DateTime]::Parse($RunStartLocal) } else { $runStart = $globalEarliest }
$brokers = @($rows | ForEach-Object broker | Select-Object -Unique)
$perBrokerTotal = if ($KeysPerBroker -gt 0) { $KeysPerBroker } elseif ($brokers.Count -gt 0) { [int][Math]::Floor($TotalPlanned / $brokers.Count) } else { 0 }

# ---------- Aggregates ----------
$agg = @()
foreach ($b in $brokers) {
    $rb = @($rows | Where-Object { $_.broker -eq $b })
    if ($rb.Count -eq 0) { continue }
    $succ = @($rb | Where-Object { $_.status -eq 'success' })
    $agg += [ordered]@{
        broker = $b
        keysTotal = $rb.Count
        keysSuccess = $succ.Count
        keysInProgress = @($rb | Where-Object { $_.status -eq 'in-progress' }).Count
        keysFailed = @($rb | Where-Object { $_.status -eq 'failed' -and $_.displayStatus -ne 'warned' }).Count
        keysWarned = @($rb | Where-Object { $_.displayStatus -eq 'warned' }).Count
        attemptsTotal = [int](@($rb | ForEach-Object attempts) | Measure-Object -Sum).Sum
        clockWarnAttempts = [int](@($rb | ForEach-Object clockWarns) | Measure-Object -Sum).Sum
        nicRejectAttempts = [int](@($rb | ForEach-Object nicRejects) | Measure-Object -Sum).Sum
        keyDuration = StatMinAvgMax ($rb | ForEach-Object keyDurationSeconds)
        attemptedRps = StatMinAvgMax ($succ | ForEach-Object attemptedRps)
        p50Ms = StatMinAvgMax ($succ | ForEach-Object p50Ms)
        p95Ms = StatMinAvgMax ($succ | ForEach-Object p95Ms)
        p99Ms = StatMinAvgMax ($succ | ForEach-Object p99Ms)
        deliveryLossAvg = if (@($succ | ForEach-Object deliveryLossRate).Count) { [math]::Round((@($succ | ForEach-Object deliveryLossRate) | Measure-Object -Average).Average, 4) } else { $null }
    }
}
# ---------- ETA / estimate ----------
$now = Get-Date
$elapsedSec = [Math]::Max(0, ($now - $runStart).TotalSeconds)
$keysDone = @($rows | Where-Object { $_.status -eq 'success' }).Count
$keysStarted = $rows.Count
$avgKeySec = if ($keysStarted -gt 0) { $elapsedSec / $keysStarted } else { 0 }
$remaining = [Math]::Max(0, $TotalPlanned - $keysDone)
$etaSec = if ($avgKeySec -gt 0) { $avgKeySec * $remaining } else { 0 }
$estFinish = $now.AddSeconds($etaSec)
$elapsedMin = [math]::Round($elapsedSec / 60, 1)

# ---------- Write JSON artifacts ----------
$rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutFolder 'key-timing.json') -Encoding UTF8
[ordered]@{
    generatedLocal = $now.ToString('yyyy-MM-dd HH:mm:ss')
    runStartLocal = $runStart.ToString('yyyy-MM-dd HH:mm:ss')
    elapsedMin = $elapsedMin
    keysSuccess = $keysDone
    keysStarted = $keysStarted
    totalPlanned = $TotalPlanned
    remainingSuccess = $remaining
    avgKeyWallSec = [math]::Round($avgKeySec, 1)
    etaMin = [math]::Round($etaSec / 60, 0)
    etaHours = [math]::Round($etaSec / 3600, 2)
    estFinishLocal = $estFinish.ToString('yyyy-MM-dd HH:mm')
    caveat = 'env-limited (link + stand clock/NIC); run may halt early on a heavy key, so ETA is an upper bound'
} | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutFolder 'estimate.json') -Encoding UTF8
$agg | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutFolder 'aggregates.json') -Encoding UTF8

# ---------- Markdown report ----------
$md = @()
$md += '# Benchmark Run Monitoring Report'
$md += ''
$md += '- Generated (local): ' + $now.ToString('yyyy-MM-dd HH:mm:ss')
$md += '- Run started (local): ' + $runStart.ToString('yyyy-MM-dd HH:mm:ss')
$md += '- Elapsed: ' + $elapsedMin + ' min'
$md += '- Campaigns prefix: ' + $CampaignPrefix + ' | Brokers: ' + ($brokers -join ', ')
$md += '- Attempt classification: clock-alignment "unready" = **WARNING** (env/time-sync; throughput valid, latency unreliable); NIC sampling-gap / counter-wrap = hard **reject** (link saturation).'
$md += '- A key whose attempts failed ONLY on clock-alignment is reported as **warned** (not failed); NIC/link rejections remain failed.'
$md += ''
$md += '## Progress'
$md += ''
$md += '```'
$md += (Progress-Bar $keysDone $TotalPlanned 40) + '   overall'
foreach ($b in $brokers) {
    $done = @($rows | Where-Object { $_.broker -eq $b -and $_.status -eq 'success' }).Count
    $md += (Progress-Bar $done $perBrokerTotal 24) + ('   ' + $b)
}
$md += '```'
$md += ''
$md += '## Progress & ETA (estimate)'
$md += ''
$md += '- Elapsed: ' + $elapsedMin + ' min'
$md += '- Keys done (success): ' + $keysDone + ' / ' + $TotalPlanned + '   (dirs started: ' + $keysStarted + ')'
$md += '- Avg key wall time (incl. retries): ' + [math]::Round($avgKeySec, 1) + ' s'
$md += '- Remaining (success keys to reach ' + $TotalPlanned + '): ' + $remaining
$md += '- **ETA (if all ' + $TotalPlanned + ' complete): ~' + [math]::Round($etaSec / 60, 0) + ' min (~' + [math]::Round($etaSec / 3600, 1) + ' h)**'
$md += '- **Estimated finish (local): ' + $estFinish.ToString('yyyy-MM-dd HH:mm') + '**'
$md += '- Caveat: env-limited (link + stand clock/NIC); the run may HALT early on a heavy key - treat ETA as an upper bound.'
$md += ''
$md += '## Per-broker summary'
$md += ''
$md += '| Broker | Keys tot/succ/inprog/failed/warned | Attempts | clock-warn / nic-reject | Key dur min/avg/max (s) | attemptedRps min/avg/max | p50 avg | p95 avg | p99 avg | deliveryLoss avg |'
$md += '|---|---|---|---|---|---|---|---|---|---|'
foreach ($a in $agg) {
    $kd = '{0}/{1}/{2}' -f $a.keyDuration.min, $a.keyDuration.avg, $a.keyDuration.max
    $ar = '{0}/{1}/{2}' -f $a.attemptedRps.min, $a.attemptedRps.avg, $a.attemptedRps.max
    $md += ('| {0} | {1}/{2}/{3}/{4}/{5} | {6} | {7} / {8} | {9} | {10} | {11} | {12} | {13} | {14} |' -f $a.broker, $a.keysTotal, $a.keysSuccess, $a.keysInProgress, $a.keysFailed, $a.keysWarned, $a.attemptsTotal, $a.clockWarnAttempts, $a.nicRejectAttempts, $kd, $ar, $a.p50Ms.avg, $a.p95Ms.avg, $a.p99Ms.avg, $a.deliveryLossAvg)
}
$md += ''
$md += '## Per-key detail'
$md += ''
$md += '| Broker | Key | Attempts | Status | clock-warn / nic-reject | Dur (s) | attemptedRps | p50/p95/p99/max (ms) | deliveryLoss | Notes |'
$md += '|---|---|---|---|---|---|---|---|---|---|'
foreach ($r in $rows) {
    $lat = if ($null -ne $r.p50Ms) { '{0}/{1}/{2}/{3}' -f $r.p50Ms, $r.p95Ms, $r.p99Ms, $r.maxMs } else { '-' }
    $ar = if ($null -ne $r.attemptedRps) { [string]$r.attemptedRps } else { '-' }
    $dl = if ($null -ne $r.deliveryLossRate) { [string]$r.deliveryLossRate } else { '-' }
    $note = if ($r.failureReasons.Count -gt 0) { (($r.failureReasons -join ' | ').Replace("`r", ' ').Replace("`n", ' ')) } else { '' }
    $cr = $r.clockWarns.ToString() + ' / ' + $r.nicRejects.ToString()
    $md += ('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |' -f $r.broker, $r.key, $r.attempts, $r.displayStatus, $cr, $r.keyDurationSeconds, $ar, $lat, $dl, $note)
}
$md += ''
Set-Content (Join-Path $OutFolder 'run-stats.md') -Value ($md -join "`r`n") -Encoding UTF8

# ---------- stdout summary ----------
Write-Output 'OK - report written'
Write-Output ('folder=' + $OutFolder)
Write-Output ('keys=' + $rows.Count + '  ' + (($agg | ForEach-Object { $_.broker + '=' + $_.keysTotal }) -join ', '))
Write-Output ('ETA: ~' + [math]::Round($etaSec / 60, 0) + ' min (~' + [math]::Round($etaSec / 3600, 1) + ' h), est finish ' + $estFinish.ToString('HH:mm') + ' local')
Write-Output ('PROGRESS: ' + (Progress-Bar $keysDone $TotalPlanned 40))
