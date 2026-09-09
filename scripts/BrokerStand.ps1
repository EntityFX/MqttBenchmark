[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('validate', 'pull', 'start', 'health', 'clock', 'fence', 'reset', 'capture', 'stop')][string]$Action,
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$OutputDirectory = 'artifacts/broker-stand',
    [ValidateRange(1, 86400)][int]$CaptureSeconds = 1,
    [string]$DockerExecutable = 'docker'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonFile($Value, [string]$Path) {
    ConvertTo-Json -InputObject $Value -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Required($Object, [string]$Name) {
    if (-not $Object.Contains($Name) -or [string]::IsNullOrWhiteSpace([string]$Object[$Name])) { throw "Missing required property '$Name'." }
    return $Object[$Name]
}

function Is-Custom($Broker) { return $Broker.name -in @('Aedes', 'ActiveMQ') }
function Is-Digest([string]$Value) { return $Value -cmatch '^sha256:[0-9a-f]{64}$' -and $Value -cnotmatch '^sha256:(.)\1{63}$' }

function Test-Inventory($Inventory) {
    if ((Required $Inventory 'schemaVersion') -ne 'broker-stand.v1') { throw 'Unsupported broker stand schema version.' }
    if ((Required $Inventory 'deploymentMode') -notin @('singleHostSequential', 'distributedHosts')) { throw 'Unsupported deployment mode.' }
    if ((Required $Inventory 'cpuMode') -notin @('singleCorePinned', 'hostAllCores')) { throw 'Unsupported CPU mode.' }
    if ((Required $Inventory 'networkMode') -ne 'host') { throw 'Direct host network mode is required.' }
    [void](Required $Inventory 'dockerContext')
    $control = [Net.IPAddress]::None
    if (-not [Net.IPAddress]::TryParse((Required $Inventory 'controlInterface'), [ref]$control) -or $control.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $control.Equals([Net.IPAddress]::Any)) {
        throw 'controlInterface must be a concrete IPv4 address.'
    }
    $loaded = @($Inventory.brokers | Where-Object { $_.loaded -eq $true })
    if ($loaded.Count -ne 1 -or $loaded[0].name -ne $Inventory.activeBroker) { throw 'Exactly the active broker must be loaded.' }
    $names = @{}; $endpoints = @{}
    foreach ($broker in $Inventory.brokers) {
        $name = Required $broker 'name'
        if ($name -notin @('Aedes', 'Mosquitto', 'ActiveMQ', 'EMQX') -or $names.ContainsKey($name)) { throw 'Unknown or duplicate broker name.' }
        $names[$name] = $true
        if ((Required $broker 'serviceName') -ne $name.ToLowerInvariant() -or (Required $broker 'containerName') -ne $name.ToLowerInvariant()) { throw 'Broker service/container name does not match the stand.' }
        $mqtt = [Uri](Required $broker 'mqttUri')
        if ($mqtt.Scheme -ne 'mqtt' -or $mqtt.Port -lt 1) { throw 'A valid mqtt URI is required.' }
        if ($endpoints.ContainsKey($mqtt.AbsoluteUri)) { throw 'Duplicate MQTT endpoint.' }
        $endpoints[$mqtt.AbsoluteUri] = $true
        [void](Required $broker 'image')
        if (Is-Custom $broker) {
            if (-not [string]::IsNullOrEmpty([string]$broker.digest)) { throw "Custom broker '$name' must leave digest empty; its local image ID is recorded by pull." }
        }
        elseif (-not (Is-Digest ([string]$broker.digest)) -or $broker.digestProvenance.status -ne 'verified') { throw "Broker '$name' requires a verified immutable registry digest." }
        if ($Inventory.cpuMode -eq 'singleCorePinned' -and [string]$broker.cpuset -notmatch '^\d+$') { throw "Broker '$name' must pin exactly one CPU." }
        if ($Inventory.cpuMode -eq 'hostAllCores' -and -not [string]::IsNullOrWhiteSpace([string]$broker.cpuset)) { throw 'hostAllCores must not define cpuset.' }
        if ([long](Required $broker 'memoryLimit') -ne 4294967296) { throw 'The baseline memory limit is 4294967296 bytes.' }
        if ([int](Required $broker 'distributedMqttPort') -ne 1883) { throw 'Distributed brokers must use MQTT port 1883.' }
        $singlePorts = @{ Aedes = 1883; Mosquitto = 2883; ActiveMQ = 3883; EMQX = 4883 }
        if ([int](Required $broker 'singleHostMqttPort') -ne $singlePorts[$name]) { throw 'Invalid single-host MQTT port.' }
        if ($broker.managementUri) {
            $management = [Uri]$broker.managementUri
            $managementPort = if ($name -eq 'ActiveMQ') { 8161 } else { 18083 }
            if ($management.Host -ne $Inventory.controlInterface -or $management.Port -ne $managementPort) { throw 'Management URI must match the configured control interface and management port.' }
        }
    }
    if ($Inventory.Contains('campaign') -and $Inventory.campaign -and ($Inventory.campaign.deploymentMode -ne $Inventory.deploymentMode -or $Inventory.campaign.cpuMode -ne $Inventory.cpuMode)) { throw 'Campaign deployment and CPU modes must match the broker stand.' }
}

function Invoke-Docker([string[]]$Arguments, [scriptblock]$OnLine = $null) {
    $result = & $DockerExecutable @Arguments 2>&1 | ForEach-Object {
        if ($OnLine) { & $OnLine ([string]$_) } else { $_ }
    }
    $code = if (Test-Path Variable:LASTEXITCODE) { $LASTEXITCODE } else { 0 }
    if ($code -ne 0) { throw "Docker command failed ($code): $($Arguments[2]) $($Arguments[3]); $($result | Out-String)" }
    return ($result | Out-String).Trim()
}

function Get-BrokersRoot {
    foreach ($candidate in @((Join-Path $PSScriptRoot 'docker/brokers'), (Join-Path $PSScriptRoot '../docker/brokers'))) {
        if (Test-Path (Join-Path $candidate 'compose.yml')) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Broker compose file was not found.'
}

function Get-Hash([string]$Path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

function Get-BuildInputs($Broker) {
    $paths = @('compose.yml', '.dockerignore')
    if ($Broker.name -eq 'Aedes') { $paths += @('Dockerfile.aedes', 'aedes/package.json', 'aedes/package-lock.json', 'aedes/server.js') }
    else { $paths += @('activemq/Dockerfile', 'activemq/activemq.xml', 'activemq/jetty-http.xml', 'activemq/log4j2.properties', 'activemq/logging.properties', 'activemq/stand-setenv') }
    return @($paths | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = Get-Hash (Join-Path $brokersRoot $_) } })
}

function Get-BuildProvenancePath($Broker) { return Join-Path $OutputDirectory "build/$($Broker.serviceName).json" }

function Assert-BuildIdentity($Broker) {
    if (-not (Is-Custom $Broker)) { return $null }
    $path = Get-BuildProvenancePath $Broker
    if (-not (Test-Path -LiteralPath $path)) { throw "Broker '$($Broker.name)' requires a recorded build identity; run pull first." }
    $record = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    if ($record.broker -ne $Broker.name -or $record.image -ne $Broker.image -or $record.context -ne $context -or -not (Is-Digest $record.imageId)) { throw 'Build provenance does not match the selected broker, context, and image.' }
    $current = @(Get-BuildInputs $Broker)
    if ($record.buildInputs.Count -ne $current.Count) { throw 'Recorded build inputs do not match current build inputs; run pull.' }
    for ($i = 0; $i -lt $current.Count; $i++) {
        if ($record.buildInputs[$i].path -ne $current[$i].path -or $record.buildInputs[$i].sha256 -ne $current[$i].sha256) { throw 'Recorded build inputs have changed; run pull.' }
    }
    $actual = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{.Id}}', $Broker.image)
    if ($actual -ne $record.imageId) { throw 'Image ID does not match recorded build provenance.' }
    return $record
}

function New-Compose($Broker, [switch]$BuildOnly) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $base = Get-Content -Raw -LiteralPath (Join-Path $brokersRoot 'compose.yml') | ConvertFrom-Json -AsHashtable
    $service = $base.services[$Broker.serviceName]
    $service.Remove('profiles') | Out-Null
    $service.image = if (Is-Custom $Broker) { $Broker.image } else { "$($Broker.image)@$($Broker.digest)" }
    if ($service.Contains('build')) { $service.build.context = $brokersRoot }
    $service.environment = [ordered]@{}
    $configs = @()
    if (-not $BuildOnly) {
        if ($inventory.cpuMode -eq 'singleCorePinned') { $service.cpuset = [string]$Broker.cpuset }
        $port = if ($inventory.deploymentMode -eq 'singleHostSequential') { $Broker.singleHostMqttPort } else { $Broker.distributedMqttPort }
        $service.environment.MQTT_PORT = [string]$port
        $service.environment.CONTROL_INTERFACE = $inventory.controlInterface
        $sources = switch ($Broker.name) {
            'Aedes' { @{ source = 'aedes/server.js'; target = '/app/server.js' } }
            'Mosquitto' { @{ source = 'mosquitto/mosquitto.conf'; target = '/mosquitto/config/mosquitto.conf' } }
            'EMQX' { @{ source = 'emqx/emqx.conf'; target = '/opt/emqx/etc/emqx.conf' } }
            'ActiveMQ' {
                @{ source = 'activemq/activemq.xml'; target = '/opt/activemq/conf/activemq.xml' }
                @{ source = 'activemq/jetty-http.xml'; target = '/opt/activemq/conf/jetty/jetty-http.xml' }
                @{ source = 'activemq/log4j2.properties'; target = '/opt/activemq/conf/log4j2.properties' }
                @{ source = 'activemq/logging.properties'; target = '/opt/activemq/conf/logging.properties' }
                @{ source = 'activemq/stand-setenv'; target = '/opt/activemq/conf/stand-setenv' }
            }
        }
        $commands = @('set -eu')
        $configMap = [ordered]@{}
        $index = 0
        foreach ($source in $sources) {
            $text = (Get-Content -Raw -LiteralPath (Join-Path $brokersRoot $source.source)).Replace("`r`n", "`n")
            $text = $text.Replace('${MQTT_PORT}', [string]$port).Replace('${CONTROL_INTERFACE}', $inventory.controlInterface)
            $path = Join-Path $OutputDirectory "effective-$($Broker.serviceName)-$index.conf"
            [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
            $variable = "MQTTBENCHMARK_CONFIG_$index"
            $service.environment[$variable] = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
            $configMap[$source.target] = $variable
            # $$ escapes Compose interpolation; the container shell receives $VARIABLE.
            $commands += ('printf ''%s'' "$$' + $variable + '" | base64 -d > ' + $source.target)
            $configs += [ordered]@{ path = $path; containerPath = $source.target; sha256 = Get-Hash $path }
            $index++
        }
        $service.environment.MQTTBENCHMARK_CONFIG_MAP = ConvertTo-Json -InputObject $configMap -Compress
        $commands += switch ($Broker.name) {
            'Aedes' { 'exec node /app/server.js' }
            'Mosquitto' { 'exec /docker-entrypoint.sh /usr/sbin/mosquitto -c /mosquitto/config/mosquitto.conf' }
            'EMQX' { 'exec /usr/bin/docker-entrypoint.sh /opt/emqx/bin/emqx foreground' }
            'ActiveMQ' { 'exec /opt/activemq/bin/activemq console' }
        }
        # Aedes owns /app at runtime so the exact server input can be transported and verified like other configs.
        if ($Broker.name -eq 'Aedes') { $service.working_dir = '/app' }
        $service.entrypoint = @('/bin/sh', '-c')
        $service.command = @($commands -join "`n")
    }
    $document = [ordered]@{ name = $base.name; services = [ordered]@{ $Broker.serviceName = $service } }
    $filename = if ($BuildOnly) { 'build-compose.yml' } else { 'effective-compose.yml' }
    $composePath = Join-Path $OutputDirectory $filename
    Write-JsonFile $document $composePath
    return @{ path = $composePath; configs = $configs }
}

function Inspect-Container($Broker) {
    return Invoke-Docker @('--context', $context, 'inspect', '--format', '{{json .}}', $Broker.containerName) | ConvertFrom-Json -AsHashtable
}

function Assert-EffectiveConfigs($Broker, $Configs) {
    $paths = @($Configs | ForEach-Object { $_.containerPath })
    $output = Invoke-Docker (@('--context', $context, 'exec', $Broker.containerName, 'sha256sum') + $paths)
    $actual = @{}
    foreach ($line in $output -split "`r?`n") {
        if ($line -match '^([a-f0-9]{64})\s+(.+)$') { $actual[$Matches[2]] = $Matches[1] }
        else { throw 'Malformed effective configuration hash response.' }
    }
    foreach ($config in $Configs) {
        if ($actual[$config.containerPath] -ne $config.sha256) { throw "Running configuration hash does not match start provenance: $($config.containerPath)." }
    }
}

function Get-Run($Broker) {
    $path = Join-Path $OutputDirectory 'run-provenance.json'
    if (-not (Test-Path $path)) { throw 'No start provenance exists in this output directory; run start first.' }
    $run = Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
    $container = Inspect-Container $Broker
    if ($run.broker -ne $Broker.name -or $run.context -ne $context -or $run.containerId -ne $container.Id -or $run.imageId -ne $container.Image -or $container.State.Status -ne 'running') { throw 'Running container does not match start provenance.' }
    $selectedImage = if (Is-Custom $Broker) { $Broker.image } else { "$($Broker.image)@$($Broker.digest)" }
    if ($run.requestedImage -ne $selectedImage -or ($build -and $run.imageId -ne $build.imageId)) { throw 'Running container image does not match the selected identity; run start.' }
    Assert-EffectiveConfigs $Broker $run.effectiveConfigs
    return $run
}

function Test-MqttReadiness([string]$MqttUri) {
    $uri = [Uri]$MqttUri
    $client = [Net.Sockets.TcpClient]::new()
    try {
        try {
            if (-not $client.ConnectAsync($uri.Host, $uri.Port).Wait(5000)) { throw 'Connect timeout.' }
        } catch { throw "MQTT readiness failed connecting to '$MqttUri'." }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 5000
        $packet = [byte[]](0x10,0x12,0x00,0x04,0x4d,0x51,0x54,0x54,0x04,0x02,0x00,0x05,0x00,0x06,0x68,0x65,0x61,0x6c,0x74,0x68)
        $stream.Write($packet, 0, $packet.Length)
        $response = [byte[]]::new(4)
        $offset = 0
        while ($offset -lt 4) {
            $count = $stream.Read($response, $offset, 4 - $offset)
            if ($count -eq 0) { throw 'MQTT readiness received incomplete CONNACK.' }
            $offset += $count
        }
        if (($response -join ',') -ne '32,2,0,0') { throw 'MQTT readiness received non-success CONNACK.' }
        $stream.Write([byte[]](0xe0,0), 0, 2)
    } finally { $client.Dispose() }
}

function Write-ClockAlignment($Broker) {
    $run = Get-Run $Broker
    $probes = @(for ($i = 0; $i -lt 3; $i++) {
        $before = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $raw = Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, 'date', '-u', '+%s')
        $timer.Stop()
        $after = [DateTimeOffset]::UtcNow
        $epoch = 0L
        if (-not [long]::TryParse($raw, [ref]$epoch)) { throw 'Remote clock returned an invalid Unix timestamp.' }
        $remote = [DateTimeOffset]::FromUnixTimeSeconds($epoch)
        # date truncates to whole seconds: centre that interval and retain its uncertainty.
        $offset = ($remote.AddMilliseconds(500) - $before.AddMilliseconds($timer.Elapsed.TotalMilliseconds / 2)).TotalMilliseconds
        $wallDrift = [Math]::Abs(($after - $before).TotalMilliseconds - $timer.Elapsed.TotalMilliseconds)
        [ordered]@{ controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O'); remoteUtc = $remote.ToString('O'); roundTripMs = $timer.Elapsed.TotalMilliseconds; offsetMs = $offset; uncertaintyMs = 500 + $timer.Elapsed.TotalMilliseconds / 2 + $wallDrift; controllerClockStepMs = $wallDrift }
    })
    $best = $probes | Sort-Object roundTripMs | Select-Object -First 1
    $ready = ([Math]::Abs($best.offsetMs) + $best.uncertaintyMs -le 2000) -and (@($probes | Where-Object { $_.controllerClockStepMs -gt 100 }).Count -eq 0)
    Write-JsonFile ([ordered]@{ ready = $ready; thresholdMs = 2000; offsetMs = $best.offsetMs; uncertaintyMs = $best.uncertaintyMs; method = 'docker-exec-date-unix-seconds-midpoint'; clockScope = 'container-realtime-shared-with-host'; context = $context; broker = $Broker.name; containerId = $run.containerId; imageId = $run.imageId; controllerMachine = [Environment]::MachineName; probes = $probes }) (Join-Path $OutputDirectory 'clock-alignment.json')
    if (-not $ready) { throw 'Controller/broker clock alignment is unready; see clock-alignment.json (2000 ms conservative bound). Synchronize clocks externally before retrying.' }
}

function Write-Capture($Broker) {
    $run = Get-Run $Broker
    $dockerVersion = Invoke-Docker @('--context', $context, 'version', '--format', '{{json .}}') | ConvertFrom-Json -AsHashtable
    $image = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{json .}}', $run.imageId) | ConvertFrom-Json -AsHashtable
    $runtime = $null
    $version = switch ($Broker.name) {
        'Aedes' {
            $node = Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, 'node', '-p', 'JSON.stringify({broker:JSON.parse(require("fs").readFileSync("/app/node_modules/aedes/package.json","utf8")).version,runtime:process.version})') | ConvertFrom-Json
            $runtime = "Node $($node.runtime)"
            "Aedes $($node.broker)"
        }
        'ActiveMQ' {
            $runtime = 'Java ' + (Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, 'java', '-version'))
            Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, '/opt/activemq/bin/activemq', '--version')
        }
        'Mosquitto' { Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, '/usr/sbin/mosquitto', '-h') }
        'EMQX' { Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, '/opt/emqx/bin/emqx', 'ctl', 'status') }
    }
    Write-JsonFile ([ordered]@{ docker = $dockerVersion; image = $image; runningImageId = $run.imageId; requestedImage = $run.requestedImage; brokerVersion = $version; runtimeVersion = $runtime }) (Join-Path $OutputDirectory 'versions.json')
    $script = (Get-Content -Raw -LiteralPath (Join-Path $brokersRoot 'telemetry.sh')).Replace("`r`n", "`n").Replace('samples=1', "samples=$CaptureSeconds")
    # One exec owns the entire monotonic schedule. Docker/SSH latency occurs once, outside the sample loop.
    $samples = [Collections.Generic.List[object]]::new()
    Invoke-Docker @('--context', $context, 'exec', $Broker.containerName, 'sh', '-c', $script) {
        param($line)
        if (-not $line.Trim()) { return }
        $sample = $line | ConvertFrom-Json -AsHashtable
        foreach ($field in @('cgroupCpuAndThrottle', 'network', 'diskIo', 'connections')) {
            $sample[$field] = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($sample["${field}Base64"]))
            $sample.Remove("${field}Base64")
        }
        $sample.container = $Broker.containerName
        $sample.cpuScope = 'container-cgroup'
        $sample.memoryScope = 'container-cgroup'
        $sample.processRssScope = 'broker-processes'
        $sample.diskIoScope = 'container-cgroup'
        $sample.networkScope = 'host-shared'
        $sample.connectionsScope = 'host-shared'
        ConvertTo-Json -InputObject $sample -Compress | Add-Content -LiteralPath (Join-Path $OutputDirectory 'telemetry.ndjson')
        $samples.Add($sample)
        if ($samples.Count -eq $CaptureSeconds) {
            # No later sample will cover work, even while Docker logs/config capture continues.
            # For a one-sample capture publish this before readiness to prevent workload dispatch.
            $endedPath = Join-Path $OutputDirectory 'sampler-ended.json'
            Write-JsonFile ([ordered]@{ lastSampleTimestamp = $sample.timestamp; lastSampleMonotonicSeconds = $sample.monotonicSeconds; sampleCount = $samples.Count; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); containerId = $run.containerId }) "$endedPath.tmp"
            Move-Item -LiteralPath "$endedPath.tmp" -Destination $endedPath
        }
        if ($samples.Count -eq 1) {
            $readyPath = Join-Path $OutputDirectory 'telemetry-ready.json'
            Write-JsonFile ([ordered]@{ firstSampleTimestamp = $sample.timestamp; firstSampleMonotonicSeconds = $sample.monotonicSeconds; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); containerId = $run.containerId }) "$readyPath.tmp"
            Move-Item -LiteralPath "$readyPath.tmp" -Destination $readyPath
        }
    }
    if ($samples.Count -lt $CaptureSeconds) { throw 'Telemetry sampler returned fewer samples than requested.' }
    $logsDirectory = Join-Path $OutputDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null
    Invoke-Docker @('--context', $context, 'logs', '--timestamps', $Broker.containerName) | Set-Content (Join-Path $logsDirectory "$($Broker.name).log")
    $hashes = @($run.inputHashes) + @($run.effectiveConfigs | ForEach-Object { @{ path = $_.containerPath; sha256 = $_.sha256; scope = 'container-effective' } })
    Write-JsonFile $hashes (Join-Path $OutputDirectory 'config-hashes.json')
}

try {
    $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $inventory = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json -AsHashtable
    Test-Inventory $inventory
    $broker = @($inventory.brokers | Where-Object { $_.name -eq $inventory.activeBroker })[0]
    $context = if ($inventory.deploymentMode -eq 'distributedHosts') { Required $broker 'dockerContext' } else { $inventory.dockerContext }
    $brokersRoot = Get-BrokersRoot
    # Cleanup must remain possible after a source edit or failed build. Inventory validation still applies.
    $build = if ($Action -notin @('pull', 'stop', 'reset')) { Assert-BuildIdentity $broker } else { $null }
    switch ($Action) {
        'validate' { $status = 'valid' }
        'pull' {
            if (Is-Custom $broker) {
                $inputs = @(Get-BuildInputs $broker)
                $compose = New-Compose $broker -BuildOnly
                Invoke-Docker @('--context', $context, 'compose', '-f', $compose.path, 'build', $broker.serviceName) | Out-Null
                $imageId = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{.Id}}', $broker.image)
                if (-not (Is-Digest $imageId)) { throw 'Build returned an invalid image ID.' }
                if ((ConvertTo-Json -InputObject $inputs -Compress) -ne (ConvertTo-Json -InputObject @(Get-BuildInputs $broker) -Compress)) { throw 'Build inputs changed during the build.' }
                New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'build') | Out-Null
                Write-JsonFile ([ordered]@{ broker = $broker.name; context = $context; image = $broker.image; imageId = $imageId; buildInputs = $inputs; capturedAt = [DateTimeOffset]::UtcNow.ToString('O') }) (Get-BuildProvenancePath $broker)
            } else { Invoke-Docker @('--context', $context, 'pull', "$($broker.image)@$($broker.digest)") | Out-Null }
            $status = 'pulled'
        }
        'start' {
            $compose = New-Compose $broker
            if ($inventory.deploymentMode -eq 'singleHostSequential') {
                $names = Invoke-Docker @('--context', $context, 'ps', '-a', '--filter', 'label=mqttbenchmark.broker=true', '--format', '{{.Names}}')
                foreach ($name in $names -split "`r?`n") {
                    if ($name -and $name -ne $broker.containerName) { Invoke-Docker @('--context', $context, 'rm', '-f', $name) | Out-Null }
                }
            }
            Invoke-Docker @('--context', $context, 'compose', '-f', $compose.path, 'up', '-d', '--no-build', $broker.serviceName) | Out-Null
            $container = Inspect-Container $broker
            if ($container.State.Status -ne 'running' -or ($build -and $container.Image -ne $build.imageId)) { throw 'Started container does not match the selected image identity.' }
            Assert-EffectiveConfigs $broker $compose.configs
            $inputPaths = @($ConfigPath, (Join-Path $brokersRoot 'compose.yml'), $PSCommandPath, $compose.path) + @($compose.configs | ForEach-Object { $_.path })
            if ($build) { $inputPaths += @($build.buildInputs | ForEach-Object { Join-Path $brokersRoot $_.path }); $inputPaths += Get-BuildProvenancePath $broker }
            $hashes = @($inputPaths | Sort-Object -Unique | ForEach-Object { @{ path = $_; sha256 = Get-Hash $_; scope = 'start-input' } })
            Write-JsonFile ([ordered]@{ broker = $broker.name; context = $context; containerId = $container.Id; imageId = $container.Image; requestedImage = $container.Config.Image; effectiveConfigs = @($compose.configs); inputHashes = $hashes; startedAt = [DateTimeOffset]::UtcNow.ToString('O') }) (Join-Path $OutputDirectory 'run-provenance.json')
            $status = 'started'
        }
        'health' { [void](Get-Run $broker); Test-MqttReadiness $broker.mqttUri; $status = 'healthy' }
        'clock' { Write-ClockAlignment $broker; $status = 'clock-aligned' }
        'fence' {
            $run = Get-Run $broker
            $before = [DateTimeOffset]::UtcNow
            $raw = Invoke-Docker @('--context', $context, 'exec', $broker.containerName, 'cat', '/proc/uptime')
            $after = [DateTimeOffset]::UtcNow
            $uptime = 0.0
            if (-not [double]::TryParse(($raw -split '\s+')[0], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$uptime) -or -not [double]::IsFinite($uptime) -or $uptime -lt 0) { throw 'Invalid remote monotonic completion fence.' }
            $path = Join-Path $OutputDirectory 'workload-end-fence.json'
            Write-JsonFile ([ordered]@{ monotonicSeconds = $uptime; method = 'docker-exec-proc-uptime-after-workload'; controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O'); context = $context; containerId = $run.containerId; imageId = $run.imageId }) "$path.tmp"
            Move-Item -LiteralPath "$path.tmp" -Destination $path
            $status = 'fenced'
        }
        'capture' { Write-Capture $broker; $status = 'captured' }
        'stop' { Invoke-Docker @('--context', $context, 'stop', $broker.containerName) | Out-Null; $status = 'stopped' }
        'reset' { Invoke-Docker @('--context', $context, 'rm', '-f', $broker.containerName) | Out-Null; $status = 'reset' }
    }
    @{ action = $Action; status = $status; broker = $broker.name } | ConvertTo-Json -Compress
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
