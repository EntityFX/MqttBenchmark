# Loaded by BrokerStand.ps1. SSH is used only as a control transport; MQTT goes directly to mqttUri.
# The remote endpoint needs PowerShell 7 and the broker assets, but no Docker or .NET benchmark SDK.
function Save-RemoteNativeFrame($Frame) {
    $relative = [string]$Frame.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
        $relative.StartsWith('/') -or $relative.Contains(':') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw 'Remote artifact path must be a safe relative path.'
    }
    $path = Join-Path $OutputDirectory $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $bytes = [Convert]::FromBase64String([string]$Frame.bytes)
    if ($Frame.append) {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    } elseif (Test-Path -LiteralPath $path) {
        # Final snapshots must exactly match already streamed evidence. Never silently overwrite it.
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($bytes)) {
            throw "Remote artifact changed after publication: $relative"
        }
    } else {
        $temporary = "$path.remote-$([Guid]::NewGuid().ToString('N')).tmp"
        [IO.File]::WriteAllBytes($temporary, $bytes)
        [IO.File]::Move($temporary, $path)
    }
}

function Invoke-RemoteNativeStand($Inventory, $HostConfig) {
    $hostConfig = $HostConfig
    $hostName = Required $hostConfig 'host'
    $user = Required $hostConfig 'user'
    if ($hostName -notmatch '^[a-zA-Z0-9._-]+$' -or $user -notmatch '^[a-zA-Z0-9._-]+$') { throw 'Invalid native SSH host or user.' }
    $selected = @($Inventory.brokers | Where-Object { $_.name -eq $Inventory.activeBroker })
    if ($selected.Count -ne 1 -or (Get-BrokerRuntime $selected[0]) -ne 'native') { throw 'nativeHost requires a selected native broker.' }
    $repository = Required $hostConfig 'repositoryRoot'
    $workRoot = Required $hostConfig 'workRoot'
    $port = if ($hostConfig.Contains('port')) { [int]$hostConfig.port } else { 22 }
    if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid native SSH port.' }
    $ssh = if ($hostConfig.Contains('sshExecutable')) { [string]$hostConfig.sshExecutable } else { 'ssh' }
    if ($IsWindows -and -not $hostConfig.Contains('sshExecutable')) {
        # Win32 OpenSSH can stall on redirected stdin in a Windows SSH session.
        # Git for Windows supplies a client that supports our streamed worker protocol.
        $gitCommand=Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($gitCommand) {
            $directory=[IO.Path]::GetDirectoryName($gitCommand.Source)
            foreach ($level in 1..3) {
                $candidate=Join-Path $directory 'usr/bin/ssh.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ssh=$candidate; break }
                $directory=[IO.Path]::GetDirectoryName($directory)
                if (-not $directory) { break }
            }
        }
    }
    $shell = if ($hostConfig.Contains('pwshExecutable')) { [string]$hostConfig.pwshExecutable } else { 'pwsh' }
    # This name is interpolated by the remote OS shell; paths with spaces can use a pwsh PATH alias.
    if ($shell -notmatch '^[a-zA-Z0-9_./:\\-]+$') { throw 'Remote pwshExecutable must be a shell-safe path without spaces (or pwsh on PATH).' }
    $payload = @{
        script = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'BrokerStand.ps1')))
        inventory = [Convert]::ToBase64String([IO.File]::ReadAllBytes($ConfigPath))
        repository = $repository; workRoot = $workRoot
        run = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($OutputDirectory + $(if ($Inventory.Contains('nativeSessionId')) { '|' + $Inventory.nativeSessionId }))))).ToLowerInvariant()
        action = $Action; seconds = $CaptureSeconds; healthTimeout = $HealthTimeoutSeconds; clockBound = $ClockAlignmentBoundMs
        captureUntilFence = [bool]($CaptureUntilFence -or ($env:MQB_NATIVE_CAPTURE_UNTIL_FENCE -eq '1' -and
            $CaptureSeconds -gt 1 -and $Inventory.Contains('campaign')))
    }
    $payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $payload -Compress)))
    $worker = @'
$ErrorActionPreference = 'Stop'
$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json -AsHashtable
function Read-SharedSnapshot([string]$File) {
    # Windows broker stdout/stderr remain open for writing during capture.
    # Snapshot the initial length with compatible sharing, rather than ReadAllBytes.
    $s=[IO.File]::Open($File,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($s.Length -gt [int]::MaxValue) { throw 'Remote artifact exceeds the supported snapshot size.' }
        $bytes=[byte[]]::new([int]$s.Length);$offset=0
        while ($offset -lt $bytes.Length) {
            $count=$s.Read($bytes,$offset,$bytes.Length-$offset)
            if ($count -le 0) { throw 'Remote artifact was truncated during capture.' }
            $offset+=$count
        }
        return ,$bytes
    } finally { $s.Dispose() }
}
if (-not [IO.Path]::IsPathRooted($p.workRoot) -or -not [IO.Path]::IsPathRooted($p.repository)) { throw 'Remote repositoryRoot and workRoot must be absolute paths for the target OS.' }
[void][IO.Directory]::CreateDirectory($p.workRoot)
$directory = Join-Path $p.workRoot $p.run
[void][IO.Directory]::CreateDirectory($directory)
$scriptPath = Join-Path $directory 'worker/BrokerStand.ps1'
$configPath = Join-Path $directory 'worker/stand.json'
[void][IO.Directory]::CreateDirectory((Split-Path $scriptPath))
# Concurrent capture and fence invoke the same worker snapshot. Verify instead of overwriting it.
foreach ($entry in @(@{path=$scriptPath; bytes=$p.script}, @{path=$configPath; bytes=$p.inventory})) {
    $bytes = [Convert]::FromBase64String($entry.bytes)
    if (Test-Path -LiteralPath $entry.path) {
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($entry.path)) -cne $entry.bytes) { throw 'Remote worker inputs changed within one attempt.' }
    } else {
        try { $s=[IO.File]::Open($entry.path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); try { $s.Write($bytes,0,$bytes.Length) } finally { $s.Dispose() } }
        catch { if (-not (Test-Path $entry.path) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($entry.path)) -cne $entry.bytes) { throw } }
    }
}
$env:MQB_NATIVE_REPOSITORY_ROOT = $p.repository
$lease = Join-Path $p.workRoot 'active-stand.json'
if ($p.action -eq 'start') {
    $s=[IO.File]::Open($lease,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $b=[Text.Encoding]::UTF8.GetBytes($p.run); $s.Write($b,0,$b.Length) } finally { $s.Dispose() }
} elseif ($p.action -in @('health','clock','capture','fence','stop','reset')) {
    if (-not (Test-Path $lease) -or [IO.File]::ReadAllText($lease) -ne $p.run) { throw 'Remote stand lease does not belong to this attempt.' }
}
$code=1
try {
    & (Get-Process -Id $PID).Path -NoProfile -File $scriptPath -NativeWorker -Action $p.action -ConfigPath $configPath -OutputDirectory $directory -CaptureSeconds $p.seconds -CaptureUntilFence:([bool]$p.captureUntilFence) -HealthTimeoutSeconds $p.healthTimeout -ClockAlignmentBoundMs $p.clockBound
    $code=$LASTEXITCODE
    if (($p.action -eq 'clock') -and ($code -eq 0)) {
        # Cold PowerShell startup and executable hashing precede these round trips.
        # Wait for the controller token so each remote timestamp is inside its measured interval.
        foreach ($index in 0..2) {
            [Console]::Out.WriteLine('__MQB_CLOCK_READY__'+$index); [Console]::Out.Flush()
            if ([Console]::In.ReadLine() -cne ('probe-'+$index)) { throw 'Invalid clock probe token.' }
            $probe=@{index=$index; remoteUtc=[DateTimeOffset]::UtcNow.ToString('O')}
            [Console]::Out.WriteLine('__MQB_CLOCK_PROBE__'+(ConvertTo-Json $probe -Compress)); [Console]::Out.Flush()
        }
    }
} finally {
    # Capture only exports its own streams/logs; concurrent fence is a separate writer.
    $names = @(switch ($p.action) {
        'start' { @('run-provenance.json') }
        'clock' { @('clock-alignment.json') }
        'fence' { @('workload-end-fence.json') }
        'capture' { @('telemetry.ndjson','telemetry-ready.json','sampler-ended.json','versions.json','config-hashes.json') }
        default { @() }
    })
    if (($p.action -eq 'capture') -or (($p.action -eq 'start') -and ($code -ne 0))) {
        $logs=Join-Path $directory 'logs'
        if (Test-Path $logs) { $names += @(Get-ChildItem $logs -File | ForEach-Object { 'logs/' + $_.Name }) }
    }
    foreach ($name in $names) {
        $file=Join-Path $directory $name
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $frame=@{path=$name; append=$false; bytes=[Convert]::ToBase64String((Read-SharedSnapshot $file))}
            [Console]::Out.WriteLine('__MQB_ARTIFACT__'+(ConvertTo-Json $frame -Compress)); [Console]::Out.Flush()
        }
    }
    if (Test-Path $lease) {
        $release = (($p.action -in @('stop','reset')) -and ($code -eq 0)) -or (($p.action -eq 'start') -and ($code -ne 0))
        if (([IO.File]::ReadAllText($lease) -eq $p.run) -and $release) { [IO.File]::Delete($lease) }
    }
}
exit $code
'@
    # Payload travels through stdin rather than a command-line argument: Windows cmd.exe has
    # an 8191-character limit and Linux caps individual arguments. Neither should cap a script.
    $worker = $worker.Replace("'__PAYLOAD__'", '[Console]::In.ReadLine()')
    $compressed = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($compressed, [IO.Compression.CompressionMode]::Compress, $true)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($worker)
        $gzip.Write($bytes, 0, $bytes.Length)
    } finally { $gzip.Dispose() }
    $workerBase64 = [Convert]::ToBase64String($compressed.ToArray())
    $compressed.Dispose()
    $bootstrap = "try { `$m=[IO.MemoryStream]::new([Convert]::FromBase64String('$workerBase64')); `$g=[IO.Compression.GZipStream]::new(`$m,[IO.Compression.CompressionMode]::Decompress); `$r=[IO.StreamReader]::new(`$g); & ([ScriptBlock]::Create(`$r.ReadToEnd())) } catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    if (($shell.Length + $encoded.Length + 40) -gt 8000) { throw 'Remote native bootstrap exceeds the Windows command-line bound.' }
    $arguments = @('-T', '-p', [string]$port, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=10')
    if ($hostConfig.Contains('identityFile') -and $hostConfig.identityFile) { $arguments += @('-i', [string]$hostConfig.identityFile) }
    if ($hostConfig.Contains('knownHostsFile') -and $hostConfig.knownHostsFile) { $arguments += @('-o', "UserKnownHostsFile=$($hostConfig.knownHostsFile)") }
    $arguments += @("$user@$hostName", "$shell -NoProfile -EncodedCommand $encoded")
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$ssh; $startInfo.UseShellExecute=$false
    $startInfo.RedirectStandardInput=$true; $startInfo.RedirectStandardOutput=$true; $startInfo.RedirectStandardError=$true
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$startInfo
    $probes=[Collections.Generic.List[object]]::new()
    $pending=$null
    $started=$false
    try {
        if (-not $process.Start()) { throw 'Could not start native SSH transport.' }
        $started=$true
        $stderr=$process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine($payloadBase64); $process.StandardInput.Flush()
        if ($Action -ne 'clock') { $process.StandardInput.Close() }
        while ($null -ne ($line=$process.StandardOutput.ReadLine())) {
            $received=[DateTimeOffset]::UtcNow
            $receivedTicks=[Diagnostics.Stopwatch]::GetTimestamp()
            if ($line.StartsWith('__MQB_ARTIFACT__', [StringComparison]::Ordinal)) {
                Save-RemoteNativeFrame ($line.Substring('__MQB_ARTIFACT__'.Length) | ConvertFrom-Json -AsHashtable)
            } elseif ($line.StartsWith('__MQB_CLOCK_READY__', [StringComparison]::Ordinal)) {
                $index=[int]$line.Substring('__MQB_CLOCK_READY__'.Length)
                if ($Action -ne 'clock' -or $null -ne $pending -or $index -ne $probes.Count -or $index -gt 2) { throw 'Unexpected clock readiness frame.' }
                $pending=@{index=$index; before=[DateTimeOffset]::UtcNow; ticks=[Diagnostics.Stopwatch]::GetTimestamp()}
                $process.StandardInput.WriteLine('probe-'+$index); $process.StandardInput.Flush()
            } elseif ($line.StartsWith('__MQB_CLOCK_PROBE__', [StringComparison]::Ordinal)) {
                $probe=$line.Substring('__MQB_CLOCK_PROBE__'.Length) | ConvertFrom-Json -AsHashtable
                if ($null -eq $pending -or $probe.index -ne $pending.index) { throw 'Unexpected clock probe frame.' }
                $remote=if ($probe.remoteUtc -is [DateTime]) { [DateTimeOffset]$probe.remoteUtc } else { [DateTimeOffset]::Parse([string]$probe.remoteUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) }
                $elapsed=1000.0*($receivedTicks-$pending.ticks)/[Diagnostics.Stopwatch]::Frequency
                $drift=[Math]::Abs(($received-$pending.before).TotalMilliseconds-$elapsed)
                $offset=($remote-$pending.before.AddMilliseconds($elapsed/2)).TotalMilliseconds
                $probes.Add(@{index=$pending.index; controllerBeforeUtc=$pending.before.ToString('O'); controllerAfterUtc=$received.ToString('O'); remoteUtc=$remote.ToString('O'); offsetMs=$offset; uncertaintyMs=1+$elapsed/2+$drift; wallClockDriftMs=$drift})
                $pending=$null
                if ($probes.Count -eq 3) { $process.StandardInput.Close() }
            } else { Write-Output $line }
        }
        $process.WaitForExit(); $code=$process.ExitCode
        $errorText=$stderr.GetAwaiter().GetResult()
        if ($errorText) { [Console]::Error.Write($errorText) }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($code -ne 0) { throw "Native SSH action '$Action' failed ($code)." }
    if ($Action -eq 'clock') {
        $path = Join-Path $OutputDirectory 'clock-alignment.json'
        $record = Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
        if ($probes.Count -ne 3) { throw 'Native SSH clock requires three completed round trips.' }
        $offset=($probes | Sort-Object offsetMs)[1].offsetMs
        $uncertainty=($probes | Measure-Object uncertaintyMs -Maximum).Maximum
        $record.offsetMs=$offset; $record.uncertaintyMs=$uncertainty
        $record.probes=$probes.ToArray()
        $record.method='ssh-native-clock-roundtrip'; $record.clockScope='remote-host-realtime'
        $record.controllerMachine=[Environment]::MachineName
        $invalid=@($probes | Where-Object { ([Math]::Abs($_.offsetMs)+$_.uncertaintyMs -gt $ClockAlignmentBoundMs) -or $_.wallClockDriftMs -gt 100 })
        $record.ready=($record.ready -and $invalid.Count -eq 0 -and ([Math]::Abs($offset)+$uncertainty -le $ClockAlignmentBoundMs))
        $record.sshHost=$hostName; $record.controllerBeforeUtc=$probes[0].controllerBeforeUtc; $record.controllerAfterUtc=$probes[2].controllerAfterUtc
        Write-JsonFile $record $path
        if (-not $record.ready) { throw 'Controller/broker clock alignment is unready; see clock-alignment.json.' }
    }
}
