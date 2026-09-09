param([Parameter(ValueFromRemainingArguments = $true)][string[]]$DockerArguments)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
ConvertTo-Json -InputObject @($DockerArguments) -Compress | Add-Content (Join-Path $PSScriptRoot 'docker-calls.ndjson')
$inventory = Get-Content (Join-Path $PSScriptRoot 'stand.json') -Raw | ConvertFrom-Json
$broker = $inventory.brokers[0]
$context = if ($inventory.deploymentMode -eq 'distributedHosts') { $broker.dockerContext } else { $inventory.dockerContext }
if ($DockerArguments[0] -ne '--context' -or $DockerArguments[1] -ne $context) { throw 'Wrong Docker context.' }
$a = @($DockerArguments | Select-Object -Skip 2)
$imageId = 'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
$joined = $a -join ' '
$statePath = Join-Path $PSScriptRoot 'container-state.json'
if ($a[0] -eq 'compose' -and $a.Count -eq 5 -and $a[1] -eq '-f' -and $a[3] -eq 'build' -and $a[4] -eq $broker.serviceName) {
    $compose = Get-Content $a[2] -Raw | ConvertFrom-Json
    if ($compose.services.($broker.serviceName).image -ne $broker.image) { throw 'Built tag does not match inventory.' }
    exit 0
}
if ($a[0] -eq 'compose' -and $a.Count -eq 7 -and $a[1] -eq '-f' -and ($a[3..5] -join ' ') -eq 'up -d --no-build' -and $a[6] -eq $broker.serviceName) {
    $compose = Get-Content $a[2] -Raw | ConvertFrom-Json -AsHashtable
    $service = $compose.services[$broker.serviceName]
    if ($service.network_mode -ne 'host' -or $service.ContainsKey('volumes') -or $service.ContainsKey('ports')) { throw 'Remote context received a bind mount or NAT.' }
    @{ Id = 'container-test-01'; Image = $imageId; State = @{ Status = 'running' }; Config = @{ Image = $service.image; Env = @($service.environment.Keys | ForEach-Object { "$_=$($service.environment[$_])" }) }; HostConfig = @{ NetworkMode = 'host'; Memory = 4294967296 }; effective = $service } | ConvertTo-Json -Depth 20 | Set-Content $statePath
    exit 0
}
if ($joined -eq "image inspect --format {{.Id}} $($broker.image)") {
    if (Test-Path (Join-Path $PSScriptRoot 'tag-image-id')) { Get-Content (Join-Path $PSScriptRoot 'tag-image-id') } else { $imageId }
    exit 0
}
if ($joined -eq "image inspect --format {{json .}} $imageId") { @{ Id = $imageId; RepoDigests = @(); Config = @{ Labels = @{} } } | ConvertTo-Json -Depth 5 -Compress; exit 0 }
if ($joined -eq 'version --format {{json .}}') { '{"Client":{"Version":"29.2.1"},"Server":{"Version":"29.2.1"}}'; exit 0 }
if ($joined -eq 'ps -a --filter label=mqttbenchmark.broker=true --format {{.Names}}') { 'inactive-stand'; $broker.containerName; exit 0 }
if ($joined -eq 'rm -f inactive-stand') { exit 0 }
if ($joined -eq "pull $($broker.image)@$($broker.digest)") { exit 0 }
if ($joined -eq "inspect --format {{json .}} $($broker.containerName)") { Get-Content $statePath -Raw; exit 0 }
if ($joined -eq "inspect --format {{.State.Status}} $($broker.containerName)") { (Get-Content $statePath -Raw | ConvertFrom-Json).State.Status; exit 0 }
if ($joined -eq "stop $($broker.containerName)" -or $joined -eq "rm -f $($broker.containerName)") {
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    $state.State.Status = 'exited'
    $state | ConvertTo-Json -Depth 20 | Set-Content $statePath
    exit 0
}
if ($joined -eq "logs --timestamps $($broker.containerName)") { '2026-09-09T00:00:00Z WARN fixture'; exit 0 }
if ($a[0] -eq 'exec' -and $a[1] -eq $broker.containerName) {
    $cmd = @($a | Select-Object -Skip 2)
    if (($cmd -join ' ') -eq 'date -u +%s') {
        if (Test-Path (Join-Path $PSScriptRoot 'clock-delay')) { Start-Sleep -Milliseconds ([int](Get-Content (Join-Path $PSScriptRoot 'clock-delay'))) }
        $offset = if (Test-Path (Join-Path $PSScriptRoot 'clock-offset')) { [int](Get-Content (Join-Path $PSScriptRoot 'clock-offset')) } else { 0 }
        [DateTimeOffset]::UtcNow.AddSeconds($offset).ToUnixTimeSeconds()
        exit 0
    }
    if ($cmd[0] -eq 'sha256sum') {
        $state = Get-Content $statePath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($path in $cmd[1..($cmd.Count - 1)]) {
            $map = $state.effective.environment.MQTTBENCHMARK_CONFIG_MAP | ConvertFrom-Json -AsHashtable
            if (-not $map.ContainsKey($path)) { throw "Hash request for unknown effective file: $path" }
            $bytes = [Convert]::FromBase64String($state.effective.environment[$map[$path]])
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            if (Test-Path (Join-Path $PSScriptRoot 'config-drift')) { $hash = '0' * 64 }
            "$hash  $path"
        }
        exit 0
    }
    if (($cmd -join ' ') -eq 'node -p JSON.stringify({broker:JSON.parse(require("fs").readFileSync("/app/node_modules/aedes/package.json","utf8")).version,runtime:process.version})' -and $broker.name -eq 'Aedes') { '{"broker":"1.1.2","runtime":"v20.20.0"}'; exit 0 }
    if (($cmd -join ' ') -eq '/opt/activemq/bin/activemq --version' -and $broker.name -eq 'ActiveMQ') { 'ActiveMQ 6.3.2'; exit 0 }
    if (($cmd -join ' ') -eq 'java -version' -and $broker.name -eq 'ActiveMQ') { 'openjdk version "21.0.12"'; exit 0 }
    if (($cmd -join ' ') -eq '/usr/sbin/mosquitto -h' -and $broker.name -eq 'Mosquitto') { 'mosquitto version 2.1.2'; exit 0 }
    if (($cmd -join ' ') -eq '/opt/emqx/bin/emqx ctl status' -and $broker.name -eq 'EMQX') { 'Node emqx@127.0.0.1 is started. EMQX version: 6.3.0'; exit 0 }
    if ($cmd.Count -eq 3 -and $cmd[0] -eq 'sh' -and $cmd[1] -eq '-c' -and $cmd[2].StartsWith('# MQTTBENCHMARK_TELEMETRY_V1')) {
        if (Test-Path (Join-Path $PSScriptRoot 'telemetry-fail')) { throw 'Sampler failed before first sample.' }
        if ($cmd[2] -notmatch 'samples=(\d+)') { throw 'Sampler omitted count.' }
        for ($i = 0; $i -lt [int]$Matches[1]; $i++) {
            if (Test-Path (Join-Path $PSScriptRoot 'telemetry-delay')) { Start-Sleep -Milliseconds ([int](Get-Content (Join-Path $PSScriptRoot 'telemetry-delay'))) }
            @{ timestamp = "2026-09-09T00:00:0${i}Z"; monotonicSeconds = 100.0 + $i; processRssBytes = 1048576; cgroupMemoryCurrentBytes = 2097152; cgroupCpuAndThrottleBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("usage_usec 123`nnr_throttled 1")); networkBase64 = ''; diskIoBase64 = ''; connectionsBase64 = ''; processIds = '1' } | ConvertTo-Json -Compress
        }
        exit 0
    }
}
throw "Unsupported Docker command: $joined"
