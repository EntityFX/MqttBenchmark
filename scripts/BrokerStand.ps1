[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('validate', 'pull', 'start', 'health', 'clock', 'fence', 'reset', 'capture', 'stop')][string]$Action,
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$OutputDirectory = 'artifacts/broker-stand',
    [ValidateRange(1, 86400)][int]$CaptureSeconds = 1,
    [ValidateRange(1, 300)][int]$HealthTimeoutSeconds = 60,
    [ValidateRange(1, 60000)][int]$ClockAlignmentBoundMs = 3000,
    [string]$DockerExecutable = 'docker',
    [string]$TrustedBuildProvenanceDirectory
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
        $runtime = if ($broker.Contains('runtime') -and $broker.runtime) { [string]$broker.runtime } else { 'container' }
        if ($runtime -notin @('container', 'native')) { throw "Unsupported broker runtime '$runtime' for broker '$name'." }
        if ($runtime -eq 'native') {
            # Native brokers run a local executable: the container "custom image" build concept does
            # not apply (no image is built or pulled), so Aedes/ActiveMQ are legal here too.
            if (-not $broker.Contains('executable') -or [string]::IsNullOrWhiteSpace([string]$broker.executable) -or -not [IO.Path]::IsPathRooted([string]$broker.executable)) { throw "Broker '$name' requires an absolute native executable path." }
            $sha = if ($broker.Contains('executableSha256')) { [string]$broker.executableSha256 } else { '' }
            if ($sha -cnotmatch '^[0-9a-f]{64}$' -or $sha -cmatch '^(.)\1{63}$') { throw "Broker '$name' requires a pinned executableSha256 (run scripts/PinNativeExecutable.ps1)." }
            foreach ($file in @($broker.configFiles)) { if ([string]::IsNullOrWhiteSpace([string]$file) -or -not [IO.Path]::IsPathRooted([string]$file)) { throw "Broker '$name' native configFiles must be absolute paths." } }
            if ($broker.Contains('args') -and $broker.args -and $broker.args -is [string]) { throw "Broker '$name' native args must be an array of strings." }
        } else {
            [void](Required $broker 'image')
            if (Is-Custom $broker) {
                if (-not [string]::IsNullOrEmpty([string]$broker.digest)) { throw "Custom broker '$name' must leave digest empty; its local image ID is recorded by pull." }
            }
            elseif (-not (Is-Digest ([string]$broker.digest)) -or $broker.digestProvenance.status -ne 'verified') { throw "Broker '$name' requires a verified immutable registry digest." }
        }
        if ($Inventory.cpuMode -eq 'singleCorePinned' -and [string]$broker.cpuset -notmatch '^\d+$') { throw "Broker '$name' must pin exactly one CPU." }
        if ($Inventory.cpuMode -eq 'hostAllCores' -and -not [string]::IsNullOrWhiteSpace([string]$broker.cpuset)) { throw 'hostAllCores must not define cpuset.' }
        if ([long](Required $broker 'memoryLimit') -ne 4294967296) { throw 'The baseline memory limit is 4294967296 bytes.' }
        if ([int](Required $broker 'distributedMqttPort') -ne 1883) { throw 'Distributed brokers must use MQTT port 1883.' }
        if (@($Inventory.brokers).Count -gt 1) {
            $singlePorts = @{ Aedes = 1883; Mosquitto = 2883; ActiveMQ = 3883; EMQX = 4883 }
            if ([int](Required $broker 'singleHostMqttPort') -ne $singlePorts[$name]) { throw 'Invalid single-host MQTT port.' }
        } else {
            # A single-broker stand (for example one native Mosquitto) may use any MQTT port.
            if (-not [int]::TryParse([string](Required $broker 'singleHostMqttPort'), [ref]$null) -or [int](Required $broker 'singleHostMqttPort') -lt 1) { throw 'Invalid single-host MQTT port.' }
        }
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

function Get-TrustedBuildProvenance($Broker, $Inputs) {
    if ([string]::IsNullOrWhiteSpace($TrustedBuildProvenanceDirectory)) { return $null }
    $trustedRoot = [IO.Path]::GetFullPath($TrustedBuildProvenanceDirectory)
    $path = Join-Path $trustedRoot "$($Broker.serviceName).json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Trusted build provenance is missing for broker '$($Broker.name)': $path" }
    $bytes = [IO.File]::ReadAllBytes($path)
    $record = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable
    foreach ($property in @('broker', 'context', 'image', 'imageId', 'buildInputs')) {
        if (-not $record.ContainsKey($property)) { throw "Trusted build provenance is missing required property '$property'." }
    }
    if ($record.broker -ne $Broker.name -or $record.context -ne $context -or $record.image -ne $Broker.image -or -not (Is-Digest ([string]$record.imageId))) {
        throw 'Trusted build provenance does not match the selected broker, context, and image.'
    }
    if ($record.buildInputs.Count -ne $Inputs.Count) { throw 'Trusted build provenance build inputs do not match current build inputs.' }
    for ($i = 0; $i -lt $Inputs.Count; $i++) {
        if ($record.buildInputs[$i].path -ne $Inputs[$i].path -or $record.buildInputs[$i].sha256 -ne $Inputs[$i].sha256) {
            throw 'Trusted build provenance build inputs do not match current build inputs.'
        }
    }
    $actual = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{.Id}}', $Broker.image)
    if ($actual -ne $record.imageId) { throw 'Image ID does not match trusted build provenance.' }
    return [ordered]@{
        broker = $Broker.name
        context = $context
        image = $Broker.image
        imageId = $actual
        buildInputs = $Inputs
        capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        source = 'trusted-existing-image'
        trustedManifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
}

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
    if ($run.broker -ne $Broker.name -or $run.context -ne $context -or $run.unitId -ne $container.Id -or $run.imageId -ne $container.Image -or $container.State.Status -ne 'running') { throw 'Running container does not match start provenance.' }
    $selectedImage = if (Is-Custom $Broker) { $Broker.image } else { "$($Broker.image)@$($Broker.digest)" }
    if ($run.requestedImage -ne $selectedImage -or ($build -and $run.imageId -ne $build.imageId)) { throw 'Running container image does not match the selected identity; run start.' }
    Assert-EffectiveConfigs $Broker $run.effectiveConfigs
    return $run
}

function Test-MqttReadiness([string]$MqttUri, [int]$TimeoutMs) {
    $uri = [Uri]$MqttUri
    $client = [Net.Sockets.TcpClient]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        try {
            if (-not $client.ConnectAsync($uri.Host, $uri.Port).Wait($TimeoutMs)) { throw 'Connect timeout.' }
        } catch { throw "MQTT readiness failed connecting to '$($uri.Host):$($uri.Port)': $($_.Exception.GetBaseException().Message)" }
        $stream = $client.GetStream()
        $stream.WriteTimeout = [Math]::Max(1, $TimeoutMs - [int]$timer.ElapsedMilliseconds)
        $packet = [byte[]](0x10,0x12,0x00,0x04,0x4d,0x51,0x54,0x54,0x04,0x02,0x00,0x05,0x00,0x06,0x68,0x65,0x61,0x6c,0x74,0x68)
        $stream.Write($packet, 0, $packet.Length)
        $response = [byte[]]::new(4)
        $offset = 0
        while ($offset -lt 4) {
            $remaining = $TimeoutMs - [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'MQTT readiness CONNACK timeout.' }
            $stream.ReadTimeout = $remaining
            $count = $stream.Read($response, $offset, 4 - $offset)
            if ($count -eq 0) { throw 'MQTT readiness received incomplete CONNACK.' }
            $offset += $count
        }
        if (($response -join ',') -ne '32,2,0,0') { throw 'MQTT readiness received non-success CONNACK.' }
        $remaining = $TimeoutMs - [int]$timer.ElapsedMilliseconds
        if ($remaining -le 0) { throw 'MQTT readiness CONNACK timeout.' }
        $stream.WriteTimeout = $remaining
        $stream.Write([byte[]](0xe0,0), 0, 2)
    } finally { $client.Dispose() }
}

function Wait-MqttReadiness($Broker) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0; $lastError = $null; $ready = $false
    while ($timer.Elapsed.TotalSeconds -lt $HealthTimeoutSeconds) {
        $attempts++
        $remaining = [int][Math]::Ceiling($HealthTimeoutSeconds * 1000 - $timer.Elapsed.TotalMilliseconds)
        try {
            Test-MqttReadiness $Broker.mqttUri ([Math]::Min(5000, [Math]::Max(1, $remaining)))
            $ready = $true
            break
        } catch { $lastError = $_.Exception.GetBaseException().Message }
        $remaining = [int][Math]::Floor($HealthTimeoutSeconds * 1000 - $timer.Elapsed.TotalMilliseconds)
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([Math]::Min(250, $remaining)) }
    }
    Write-JsonFile ([ordered]@{ ready = $ready; attempts = $attempts; timeoutSeconds = $HealthTimeoutSeconds; elapsedMs = $timer.Elapsed.TotalMilliseconds; lastError = $lastError }) (Join-Path $OutputDirectory 'health-readiness.json')
    if (-not $ready) { throw "MQTT readiness timed out after $HealthTimeoutSeconds seconds. Last error: $lastError" }
}

# --- Native (non-Docker) runtime: the broker runs as a local process, cross-platform (Windows/Linux). ---

function Get-BrokerRuntime($Broker) { if ($Broker.Contains('runtime') -and $Broker.runtime) { return [string]$Broker.runtime }; return 'container' }

function Test-NativePortFree([string]$MqttUri) {
    $uri = [Uri]$MqttUri
    $client = [Net.Sockets.TcpClient]::new()
    try { if ($client.ConnectAsync($uri.Host, $uri.Port).Wait(1000)) { return $false } } finally { $client.Dispose() }
    return $true
}

# Resolves the pid of the process listening on the given port (cross-platform).
function Get-NativeListenerPid([int]$Port) {
    if ($env:OS -eq 'Windows_NT') {
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($listener) { return [int]$listener.OwningProcess }
        return $null
    }
    $raw = $null
    try { $raw = (& ss -lptn "sport = :$Port" 2>&1 | ForEach-Object { [string]$_ }) -join "`n" } catch { $raw = $null }
    if ($raw -match 'pid=(\d+)') { return [int]$Matches[1] }
    # Fallback without ss: /proc/net/tcp socket inode -> /proc/<pid>/fd link.
    $hex = ('{0:x4}' -f $Port)
    $inode = $null
    foreach ($file in @('/proc/net/tcp', '/proc/net/tcp6')) {
        if (-not (Test-Path $file)) { continue }
        foreach ($line in (Get-Content -LiteralPath $file | Select-Object -Skip 1)) {
            $parts = $line.Trim() -split '\s+'
            if ($parts.Count -ge 10 -and $parts[1].EndsWith(':' + $hex) -and $parts[3] -eq '0A') { $inode = $parts[9]; break }
        }
        if ($inode) { break }
    }
    if ($null -ne $inode) {
        foreach ($pidDir in (Get-ChildItem -LiteralPath '/proc' -Directory -ErrorAction SilentlyContinue)) {
            if ($pidDir.Name -match '^\d+$') {
                try {
                    foreach ($link in (Get-ChildItem -LiteralPath (Join-Path $pidDir.FullName 'fd') -ErrorAction Stop | Select-Object -First 512)) {
                        if ($link.LinkTarget -eq "socket:[$inode]") { return [int]$pidDir.Name }
                    }
                } catch { $null }
            }
        }
    }
    return $null
}

function Get-NativeRun($Broker) {
    $path = Join-Path $OutputDirectory 'run-provenance.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'No native start provenance exists; run start first.' }
    $run = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    if ($run.broker -ne $Broker.name -or $run.context -ne $context) { throw 'Native run provenance does not match the selected broker/context.' }
    $process = Get-Process -Id ([int]$run.unitId) -ErrorAction SilentlyContinue
    if (-not $process) { throw "Native broker process $($run.unitId) is not running." }
    $actualPath = $null
    try { $actualPath = $process.Path } catch { $null }
    if ($actualPath -and ([IO.Path]::GetFullPath($actualPath) -ine [IO.Path]::GetFullPath([string]$Broker.executable))) {
        throw 'Running process executable does not match the pinned native identity.'
    }
    foreach ($config in @($run.effectiveConfigs)) {
        if (-not (Test-Path -LiteralPath $config.path) -or (Get-Hash $config.path) -ne $config.sha256) { throw "Native configuration changed after start: $($config.path)" }
    }
    return $run
}

function Start-NativeBroker($Broker) {
    $executable = [IO.Path]::GetFullPath([string]$Broker.executable)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Native executable not found: $executable" }
    $actualSha = Get-Hash $executable
    if ($actualSha -ne ([string]$Broker.executableSha256).ToLowerInvariant()) { throw 'Native executable hash does not match the pinned identity.' }
    if (-not (Test-NativePortFree $Broker.mqttUri)) { throw "MQTT endpoint $($Broker.mqttUri) is already in use; stop the other broker first." }
    $configs = @()
    foreach ($file in @($Broker.configFiles)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Native config file not found: $file" }
        $configs += [ordered]@{ path = [IO.Path]::GetFullPath([string]$file); sha256 = Get-Hash ([string]$file) }
    }
    $logsDirectory = Join-Path $OutputDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null
    $stdoutLog = Join-Path $logsDirectory 'broker.stdout.log'
    $stderrLog = Join-Path $logsDirectory 'broker.stderr.log'
    $affinityMask = if ($inventory.cpuMode -eq 'singleCorePinned') { [long](1L -shl [int]$Broker.cpuset) } else { $null }
    # The broker's stdio MUST go to log files, detached from the controller's console: a child that
    # inherits the controller's stdout/stderr pipes keeps them open for its whole lifetime, and any
    # parent that reads the controller output to EOF (C# StandLifecycle, PowerShell captures) blocks
    # until the broker exits. Start-Process -Redirect* leaves the same internal pipe in the child,
    # so the launch goes through the system shell with file redirection (empirically verified: the
    # parent's output capture returns immediately and the broker survives the controller exit).
    $argumentLine = @($Broker.args | ForEach-Object { '"' + ([string]$_ -replace '"', '\"') + '"' }) -join ' '
    $workingDirectory = if ($Broker.Contains('workingDirectory') -and $Broker.workingDirectory) { [IO.Path]::GetFullPath([string]$Broker.workingDirectory) } else { $logsDirectory }
    $quotedWorking = '"' + ($workingDirectory -replace '"', '\"') + '"'
    if ($env:OS -eq 'Windows_NT') {
        $commandLine = 'cd /d {0} && start /b "" "{1}" {2} > "{3}" 2> "{4}"' -f $quotedWorking, $executable, $argumentLine, $stdoutLog, $stderrLog
        $launcher = 'cmd'
    } else {
        $commandLine = 'cd {0} && nohup "{1}" {2} > "{3}" 2> "{4}" < /dev/null &' -f $quotedWorking, $executable, $argumentLine, $stdoutLog, $stderrLog
        $launcher = 'sh'
    }
    try {
        # Start-Process (not a direct native call): empirically a Start-Process-launched shell returns
        # to the controller immediately after the broker detaches, while a direct `& cmd` invocation
        # keeps the controller's output pipe open until the broker exits (parent capture would block).
        $null = Start-Process -FilePath $launcher -ArgumentList @('/c', $commandLine) -PassThru
    } catch { throw "Native broker process failed to start: $($_.Exception.Message)" }
    # Readiness + identity: the broker must own its MQTT port (the port was verified free above, so
    # the first listener is ours). The port also uniquely identifies the broker pid across the
    # shell-detached process tree.
    $port = [int]([Uri]$Broker.mqttUri).Port
    $brokerPid = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $brokerPid = Get-NativeListenerPid $port
        if ($brokerPid) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $brokerPid) { throw "Native broker did not bind its MQTT port ($($Broker.mqttUri)) within 20s; check its log in $logsDirectory." }
    $process = Get-Process -Id $brokerPid -ErrorAction SilentlyContinue
    if (-not $process) { throw "Native broker pid $brokerPid resolved from the port listener but the process is not visible." }
    # CPU pinning via Process.ProcessorAffinity is cross-platform (.NET Core: Windows mask / sched_setaffinity).
    # It is applied right after start; the workload begins only after health+clock, so no unpinned work is measured.
    if ($null -ne $affinityMask) {
        try { $process.ProcessorAffinity = [IntPtr]$affinityMask } catch { throw "Failed to pin native broker to the requested CPU set: $($_.Exception.Message)" }
    }
    # Best-effort memory limit: a Linux cgroup survives the controller exit (kernel state); Windows job objects
    # kill processes when the last handle closes, which is unsafe in a per-action controller - recorded honestly.
    $memoryEnforcement = 'not-enforced-native'
    if ($env:OS -eq 'Linux' -and (Test-Path '/sys/fs/cgroup/cgroup.controllers')) {
        try {
            $group = "/sys/fs/cgroup/mqttbenchmark-$($process.Id)"
            [void][IO.Directory]::CreateDirectory($group)
            Set-Content -LiteralPath (Join-Path $group 'memory.max') -Value ([string]$Broker.memoryLimit)
            Set-Content -LiteralPath (Join-Path $group 'cgroup.procs') -Value ([string]$process.Id)
            $memoryEnforcement = 'cgroup-v2'
        } catch { $memoryEnforcement = "cgroup-v2-unavailable: $($_.Exception.Message)" }
    }
    $inputPaths = @($ConfigPath, $PSCommandPath, $executable) + @($configs | ForEach-Object { $_.path }) | Sort-Object -Unique
    $hashes = @($inputPaths | ForEach-Object { [ordered]@{ path = $_; sha256 = Get-Hash $_; scope = 'start-input' } })
    Write-JsonFile ([ordered]@{
        broker = $Broker.name; context = $context; runtime = 'native'
        unitId = [string]$process.Id
        imageId = $actualSha; requestedImage = $executable
        effectiveConfigs = $configs
        inputHashes = $hashes
        affinityCpus = if ($inventory.cpuMode -eq 'singleCorePinned') { [string]$Broker.cpuset } else { 'all' }
        memoryLimitBytes = [long]$Broker.memoryLimit; memoryLimitEnforcement = $memoryEnforcement
        monotonicEpoch = [Diagnostics.Stopwatch]::GetTimestamp()
        monotonicFrequency = [Diagnostics.Stopwatch]::Frequency
        startedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }) (Join-Path $OutputDirectory 'run-provenance.json')
}
function Get-NativeConnections([int]$Port) {
    $hex = ('{0:x4}' -f $Port)
    if ($env:OS -eq 'Windows_NT') {
        return [int]((Get-NetTCPConnection -LocalPort $Port -State Established -ErrorAction SilentlyContinue | Measure-Object).Count)
    }
    $count = 0
    foreach ($file in @('/proc/net/tcp', '/proc/net/tcp6')) {
        if (-not (Test-Path $file)) { continue }
        foreach ($line in (Get-Content -LiteralPath $file | Select-Object -Skip 1)) {
            $parts = $line.Trim() -split '\s+'
            if ($parts.Count -ge 4 -and $parts[1].EndsWith(':' + $hex) -and $parts[3] -eq '01') { $count++ }
        }
    }
    return $count
}

function Get-NativeNetworkCounters([string]$MqttUri) {
    $uri = [Uri]$MqttUri
    $isLoopbackHost = ([Net.IPAddress]::TryParse($uri.Host, [ref]$null) -and [Net.IPAddress]::IsLoopback([Net.IPAddress]::Parse($uri.Host))) -or $uri.Host -eq 'localhost'
    $rx = [long]0; $tx = [long]0; $name = 'unknown'
    if ($env:OS -eq 'Windows_NT') {
        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($adapter.OperationalStatus -ne [Net.NetworkInformation.OperationalStatus]::Up) { continue }
            $isLoop = $adapter.NetworkInterfaceType -eq [Net.NetworkInformation.NetworkInterfaceType]::Loopback
            if ($isLoop -ne $isLoopbackHost) { continue }
            $stats = $adapter.GetIPStatistics()
            $rx += [long]$stats.BytesReceived; $tx += [long]$stats.BytesSent
            if ($name -eq 'unknown') { $name = $adapter.Name }
        }
    } else {
        foreach ($line in (Get-Content -LiteralPath '/proc/net/dev' | Select-Object -Skip 2)) {
            $separator = $line.IndexOf(':')
            if ($separator -lt 0) { continue }
            $iface = $line.Substring(0, $separator).Trim()
            if ($isLoopbackHost -and $iface -ne 'lo') { continue }
            $skip = $false
            if (-not $isLoopbackHost) {
                foreach ($prefix in @('lo', 'veth', 'tap', 'docker', 'br-')) { if ($iface.StartsWith($prefix)) { $skip = $true } }
            }
            if ($skip) { continue }
            $fields = $line.Substring($separator + 1).Split(@(' ', "`t"), [StringSplitOptions]::RemoveEmptyEntries)
            if ($fields.Count -lt 9) { continue }
            $rx += [long]$fields[0]; $tx += [long]$fields[8]
            if ($name -eq 'unknown') { $name = $iface }
        }
    }
    return [ordered]@{ interface = $name; rxBytes = $rx; txBytes = $tx }
}

function Get-NativeDiskIo([int]$ProcessId) {
    if ($env:OS -eq 'Windows_NT') { return $null }
    $path = "/proc/$ProcessId/io"
    if (-not (Test-Path $path)) { return $null }
    try {
        $values = @{}
        foreach ($line in (Get-Content -LiteralPath $path)) {
            $parts = $line -split ': '
            if ($parts.Count -eq 2 -and [long]::TryParse($parts[1], [ref]$null)) { $values[$parts[0]] = [long]$parts[1] }
        }
        if (-not ($values.ContainsKey('read_bytes') -and $values.ContainsKey('write_bytes'))) { return $null }
        return [ordered]@{ readBytes = $values['read_bytes']; writeBytes = $values['write_bytes'] }
    } catch { return $null }
}
function Write-ClockAlignmentNative($Broker) {
    $run = Get-NativeRun $Broker
    $probes = @(for ($i = 0; $i -lt 3; $i++) {
        $before = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $ok = $true; $probeError = $null
        try { Test-MqttReadiness $Broker.mqttUri 5000 } catch { $ok = $false; $probeError = $_.Exception.GetBaseException().Message }
        $timer.Stop()
        $after = [DateTimeOffset]::UtcNow
        $wallDrift = [Math]::Abs(($after - $before).TotalMilliseconds - $timer.ElapsedMilliseconds)
        [ordered]@{
            controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O')
            ok = $ok; error = $probeError
            # Same host: the controller clock IS the broker clock; the CONNACK round trip bounds RTT uncertainty.
            offsetMs = 0
            uncertaintyMs = 1 + $timer.Elapsed.TotalMilliseconds / 2 + $wallDrift
            controllerClockStepMs = $wallDrift
        }
    })
    $best = @($probes | Where-Object { $_.ok }) | Sort-Object { $_.uncertaintyMs } | Select-Object -First 1
    $ready = ($null -ne $best) -and ([Math]::Abs($best.offsetMs) + $best.uncertaintyMs -le $ClockAlignmentBoundMs) -and (@($probes | Where-Object { $_.controllerClockStepMs -gt 100 }).Count -eq 0)
    Write-JsonFile ([ordered]@{ ready = $ready; thresholdMs = $ClockAlignmentBoundMs; offsetMs = if ($best) { $best.offsetMs } else { $null }; uncertaintyMs = if ($best) { $best.uncertaintyMs } else { $null }; method = 'same-host-local-process-connack-roundtrip'; clockScope = 'same-host-realtime'; timestampResolutionBoundMs = 1; context = $context; broker = $Broker.name; unitId = [string]$run.unitId; imageId = $run.imageId; controllerMachine = [Environment]::MachineName; probes = $probes }) (Join-Path $OutputDirectory 'clock-alignment.json')
    if (-not $ready) { throw 'Controller/broker clock alignment is unready; see clock-alignment.json.' }
}

function Write-FenceNative($Broker) {
    $run = Get-NativeRun $Broker
    if (-not $run.Contains('monotonicEpoch') -or -not $run.Contains('monotonicFrequency') -or [long]$run.monotonicFrequency -le 0) { throw 'Native run provenance is missing the monotonic epoch/frequency.' }
    $before = [DateTimeOffset]::UtcNow
    # Stopwatch ticks are a machine-wide monotonic counter (QueryPerformanceCounter / CLOCK_MONOTONIC),
    # shared across controller invocations by the epoch recorded at start.
    $uptime = ([Diagnostics.Stopwatch]::GetTimestamp() - [long]$run.monotonicEpoch) / [double]$run.monotonicFrequency
    $after = [DateTimeOffset]::UtcNow
    if (-not [double]::IsFinite($uptime) -or $uptime -lt 0) { throw 'Invalid native monotonic completion fence.' }
    $path = Join-Path $OutputDirectory 'workload-end-fence.json'
    Write-JsonFile ([ordered]@{ monotonicSeconds = $uptime; method = 'native-stopwatch-epoch-after-workload'; controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O'); context = $context; unitId = [string]$run.unitId; imageId = $run.imageId }) "$path.tmp"
    Move-Item -LiteralPath "$path.tmp" -Destination $path
}

function Stop-NativeBroker($Broker, [switch]$AllowAlreadyStopped) {
    $run = $null
    try { $run = Get-NativeRun $Broker } catch { if (-not $AllowAlreadyStopped) { throw } }
    if (-not $run) { return }
    $process = Get-Process -Id ([int]$run.unitId) -ErrorAction SilentlyContinue
    if ($process) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        if (-not $process.HasExited) { throw 'Native broker did not stop after termination.' }
    }
}
function Write-CaptureNative($Broker) {
    $run = Get-NativeRun $Broker
    $version = $null
    if ($Broker.Contains('versionArgs') -and $Broker.versionArgs) {
        try { $version = ((& ([string]$Broker.executable) @([string[]]$Broker.versionArgs) 2>&1) | ForEach-Object { [string]$_ }) -join ' ' } catch { $version = $null }
    }
    Write-JsonFile ([ordered]@{
        host = [ordered]@{ os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture; machine = [Environment]::MachineName }
        executable = [ordered]@{ path = $run.requestedImage; sha256 = $run.imageId }
        brokerVersion = $version
        runtimeVersion = "PowerShell $($PSVersionTable.PSVersion) controller"
        affinityCpus = $run.affinityCpus
        memoryLimitBytes = [long]$run.memoryLimitBytes
        memoryLimitEnforcement = $run.memoryLimitEnforcement
    }) (Join-Path $OutputDirectory 'versions.json')
    $epoch = [long]$run.monotonicEpoch
    $frequency = [double]$run.monotonicFrequency
    $port = [int]([Uri]$Broker.mqttUri).Port
    $pinnedCores = if ($inventory.cpuMode -eq 'singleCorePinned') { 1 } else { [Environment]::ProcessorCount }
    $ndjsonPath = Join-Path $OutputDirectory 'telemetry.ndjson'
    $samples = [Collections.Generic.List[object]]::new()
    $previous = $null
    for ($i = 0; $i -lt $CaptureSeconds; $i++) {
        # Monotonic deadlines from the shared epoch: scheduling jitter does not shift the sample grid.
        $deadline = $epoch + [long]($i * $frequency)
        while ([Diagnostics.Stopwatch]::GetTimestamp() -lt $deadline) { Start-Sleep -Milliseconds 20 }
        $monotonic = ([Diagnostics.Stopwatch]::GetTimestamp() - $epoch) / $frequency
        $process = Get-Process -Id ([int]$run.unitId) -ErrorAction SilentlyContinue
        if (-not $process) { throw "Native broker process $($run.unitId) disappeared during sampling." }
        $cpuTotalSeconds = $process.TotalProcessorTime.TotalSeconds
        $cpuPercent = $null
        if ($null -ne $previous) {
            $intervalSeconds = [Math]::Max(0.05, $monotonic - $previous.monotonic)
            $delta = [Math]::Max(0, $cpuTotalSeconds - $previous.cpuTotalSeconds)
            $cpuPercent = [Math]::Round([Math]::Min(100 * $pinnedCores, ($delta / $intervalSeconds) * 100 / $pinnedCores), 3)
        }
        $sample = [ordered]@{
            timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            monotonicSeconds = $monotonic
            broker = $Broker.name
            unitId = [string]$run.unitId
            processRssBytes = [long]$process.WorkingSet64
            processIds = [string]$run.unitId
            memoryCurrentBytes = [long]$process.WorkingSet64
            processCpuPercent = $cpuPercent
            cpuScope = 'process-pinned'
            memoryScope = 'broker-process'
            network = (Get-NativeNetworkCounters $Broker.mqttUri)
            networkScope = 'host-shared'
            diskIo = (Get-NativeDiskIo ([int]$run.unitId))
            diskIoScope = 'broker-process'
            connections = (Get-NativeConnections $port)
            connectionsScope = 'host-shared'
        }
        ConvertTo-Json -InputObject $sample -Compress | Add-Content -LiteralPath $ndjsonPath
        $samples.Add($sample)
        $previous = [ordered]@{ monotonic = $monotonic; cpuTotalSeconds = $cpuTotalSeconds }
        if ($samples.Count -eq $CaptureSeconds) {
            $endedPath = Join-Path $OutputDirectory 'sampler-ended.json'
            Write-JsonFile ([ordered]@{ lastSampleTimestamp = $sample.timestamp; lastSampleMonotonicSeconds = $sample.monotonicSeconds; sampleCount = $samples.Count; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); unitId = [string]$run.unitId }) "$endedPath.tmp"
            Move-Item -LiteralPath "$endedPath.tmp" -Destination $endedPath
        }
        if ($samples.Count -eq 1) {
            $readyPath = Join-Path $OutputDirectory 'telemetry-ready.json'
            Write-JsonFile ([ordered]@{ firstSampleTimestamp = $sample.timestamp; firstSampleMonotonicSeconds = $sample.monotonicSeconds; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); unitId = [string]$run.unitId }) "$readyPath.tmp"
            Move-Item -LiteralPath "$readyPath.tmp" -Destination $readyPath
        }
    }
    if ($samples.Count -lt $CaptureSeconds) { throw 'Telemetry sampler returned fewer samples than requested.' }
    $logsDirectory = Join-Path $OutputDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null
    $logSource = $null
    if ($Broker.Contains('logFile') -and $Broker.logFile) {
        if ([IO.Path]::IsPathRooted([string]$Broker.logFile)) { $logSource = [string]$Broker.logFile }
        else { $logSource = Join-Path (Split-Path ([string]$Broker.executable)) ([string]$Broker.logFile) }
    }
    if ($logSource -and (Test-Path -LiteralPath $logSource)) { Copy-Item -LiteralPath $logSource -Destination (Join-Path $logsDirectory "$($Broker.name).log") -Force }
    $hashes = @($run.inputHashes) + @($run.effectiveConfigs | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256; scope = 'native-effective' } })
    Write-JsonFile $hashes (Join-Path $OutputDirectory 'config-hashes.json')
}

function Write-ClockAlignment($Broker) {
    $run = Get-Run $Broker
    $probes = @(for ($i = 0; $i -lt 3; $i++) {
        $before = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $raw = Invoke-Docker @('--context', $context, 'info', '--format', '{{.SystemTime}}')
        $timer.Stop()
        $after = [DateTimeOffset]::UtcNow
        $remote = [DateTimeOffset]::MinValue
        if ($raw -notmatch '^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$' -or -not [DateTimeOffset]::TryParse($raw, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$remote)) { throw 'Docker daemon SystemTime returned an invalid timestamp.' }
        # The daemon provides RFC3339Nano on every broker image; 1 ms conservatively covers parsing resolution.
        $offset = ($remote - $before.AddMilliseconds($timer.Elapsed.TotalMilliseconds / 2)).TotalMilliseconds
        $wallDrift = [Math]::Abs(($after - $before).TotalMilliseconds - $timer.Elapsed.TotalMilliseconds)
        [ordered]@{ controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O'); remoteUtc = $remote.ToString('O'); remoteSystemTime = $raw; roundTripMs = $timer.Elapsed.TotalMilliseconds; offsetMs = $offset; uncertaintyMs = 1 + $timer.Elapsed.TotalMilliseconds / 2 + $wallDrift; controllerClockStepMs = $wallDrift }
    })
    $best = $probes | Sort-Object { $_.roundTripMs } | Select-Object -First 1
    $ready = ([Math]::Abs($best.offsetMs) + $best.uncertaintyMs -le $ClockAlignmentBoundMs) -and (@($probes | Where-Object { $_.controllerClockStepMs -gt 100 }).Count -eq 0)
    Write-JsonFile ([ordered]@{ ready = $ready; thresholdMs = $ClockAlignmentBoundMs; offsetMs = $best.offsetMs; uncertaintyMs = $best.uncertaintyMs; method = 'docker-daemon-system-time-midpoint'; clockScope = 'docker-daemon-host-realtime'; timestampResolutionBoundMs = 1; context = $context; broker = $Broker.name; unitId = $run.unitId; imageId = $run.imageId; controllerMachine = [Environment]::MachineName; probes = $probes }) (Join-Path $OutputDirectory 'clock-alignment.json')
    if (-not $ready) { throw 'Controller/broker clock alignment is unready; see clock-alignment.json ($ClockAlignmentBoundMs ms conservative bound). Check offsets and probe uncertainty before retrying.' }
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
            Write-JsonFile ([ordered]@{ lastSampleTimestamp = $sample.timestamp; lastSampleMonotonicSeconds = $sample.monotonicSeconds; sampleCount = $samples.Count; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); unitId = $run.unitId }) "$endedPath.tmp"
            Move-Item -LiteralPath "$endedPath.tmp" -Destination $endedPath
        }
        if ($samples.Count -eq 1) {
            $readyPath = Join-Path $OutputDirectory 'telemetry-ready.json'
            Write-JsonFile ([ordered]@{ firstSampleTimestamp = $sample.timestamp; firstSampleMonotonicSeconds = $sample.monotonicSeconds; controllerObservedUtc = [DateTimeOffset]::UtcNow.ToString('O'); unitId = $run.unitId }) "$readyPath.tmp"
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
    $build = if ($Action -notin @('pull', 'stop', 'reset') -and (Get-BrokerRuntime $broker) -eq 'container') { Assert-BuildIdentity $broker } else { $null }
    $native = (Get-BrokerRuntime $broker) -eq 'native'
    switch ($Action) {
        'validate' { $status = 'valid' }
        'pull' {
            if ($native) { $status = 'skipped-native' }
            elseif (Is-Custom $broker) {
                $inputs = @(Get-BuildInputs $broker)
                New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'build') | Out-Null
                $trusted = Get-TrustedBuildProvenance $broker $inputs
                if ($trusted) { Write-JsonFile $trusted (Get-BuildProvenancePath $broker) }
                else {
                    $compose = New-Compose $broker -BuildOnly
                    Invoke-Docker @('--context', $context, 'compose', '-f', $compose.path, 'build', $broker.serviceName) | Out-Null
                    $imageId = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{.Id}}', $broker.image)
                    if (-not (Is-Digest $imageId)) { throw 'Build returned an invalid image ID.' }
                    if ((ConvertTo-Json -InputObject $inputs -Compress) -ne (ConvertTo-Json -InputObject @(Get-BuildInputs $broker) -Compress)) { throw 'Build inputs changed during the build.' }
                    Write-JsonFile ([ordered]@{ broker = $broker.name; context = $context; image = $broker.image; imageId = $imageId; buildInputs = $inputs; capturedAt = [DateTimeOffset]::UtcNow.ToString('O'); source = 'built' }) (Get-BuildProvenancePath $broker)
                }
            } else { Invoke-Docker @('--context', $context, 'pull', "$($broker.image)@$($broker.digest)") | Out-Null }
            if (-not $native) { $status = 'pulled' }
        }
        'start' {
            if ($native) { Start-NativeBroker $broker; $status = 'started' }
            else {
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
                Write-JsonFile ([ordered]@{ broker = $broker.name; context = $context; runtime = 'container'; unitId = $container.Id; imageId = $container.Image; requestedImage = $container.Config.Image; effectiveConfigs = @($compose.configs); inputHashes = $hashes; startedAt = [DateTimeOffset]::UtcNow.ToString('O') }) (Join-Path $OutputDirectory 'run-provenance.json')
                $status = 'started'
            }
        }
        'health' {
            if ($native) { [void](Get-NativeRun $broker); Wait-MqttReadiness $broker; $status = 'healthy' }
            else { [void](Get-Run $broker); Wait-MqttReadiness $broker; $status = 'healthy' }
        }
        'clock' {
            if ($native) { Write-ClockAlignmentNative $broker } else { Write-ClockAlignment $broker }
            $status = 'clock-aligned'
        }
        'fence' {
            if ($native) { Write-FenceNative $broker; $status = 'fenced' }
            else {
                $run = Get-Run $broker
                $before = [DateTimeOffset]::UtcNow
                $raw = Invoke-Docker @('--context', $context, 'exec', $broker.containerName, 'cat', '/proc/uptime')
                $after = [DateTimeOffset]::UtcNow
                $uptime = 0.0
                if (-not [double]::TryParse(($raw -split '\s+')[0], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$uptime) -or -not [double]::IsFinite($uptime) -or $uptime -lt 0) { throw 'Invalid remote monotonic completion fence.' }
                $path = Join-Path $OutputDirectory 'workload-end-fence.json'
                Write-JsonFile ([ordered]@{ monotonicSeconds = $uptime; method = 'docker-exec-proc-uptime-after-workload'; controllerBeforeUtc = $before.ToString('O'); controllerAfterUtc = $after.ToString('O'); context = $context; unitId = $run.unitId; imageId = $run.imageId }) "$path.tmp"
                Move-Item -LiteralPath "$path.tmp" -Destination $path
                $status = 'fenced'
            }
        }
        'capture' { if ($native) { Write-CaptureNative $broker } else { Write-Capture $broker }; $status = 'captured' }
        'stop' { if ($native) { Stop-NativeBroker $broker } else { Invoke-Docker @('--context', $context, 'stop', $broker.containerName) | Out-Null }; $status = 'stopped' }
        'reset' {
            if ($native) { Stop-NativeBroker $broker -AllowAlreadyStopped }
            else { Invoke-Docker @('--context', $context, 'rm', '-f', $broker.containerName) | Out-Null }
            $status = 'reset'
        }
    }
    @{ action = $Action; status = $status; broker = $broker.name } | ConvertTo-Json -Compress
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
