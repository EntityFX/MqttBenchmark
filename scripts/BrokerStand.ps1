[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('validate', 'pull', 'start', 'health', 'reset', 'capture', 'stop')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [string]$OutputDirectory = 'artifacts/broker-stand',

    [ValidateRange(1, 86400)]
    [int]$CaptureSeconds = 1,

    [string]$DockerExecutable = 'docker'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-ControllerResult {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$Message
    )

    [ordered]@{
        action = $Action
        status = $Status
        message = $Message
    } | ConvertTo-Json -Compress
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Missing required property '$Name'."
    }

    return $property.Value
}

function Test-BrokerStandInventory {
    param([Parameter(Mandatory = $true)]$Inventory)

    if ((Get-RequiredProperty $Inventory 'schemaVersion') -ne 'broker-stand.v1') {
        throw 'Unsupported broker stand schema version.'
    }

    $deploymentMode = [string](Get-RequiredProperty $Inventory 'deploymentMode')
    if ($deploymentMode -notin @('singleHostSequential', 'distributedHosts')) {
        throw "Unsupported deployment mode '$deploymentMode'."
    }

    $cpuMode = [string](Get-RequiredProperty $Inventory 'cpuMode')
    if ($cpuMode -notin @('singleCorePinned', 'hostAllCores')) {
        throw "Unsupported CPU mode '$cpuMode'."
    }

    [void](Get-RequiredProperty $Inventory 'activeBroker')
    [void](Get-RequiredProperty $Inventory 'dockerContext')
    $brokers = @($Inventory.brokers)
    if ($brokers.Count -eq 0) {
        throw 'At least one broker inventory entry is required.'
    }

    $loaded = @($brokers | Where-Object { $_.loaded -eq $true })
    if ($loaded.Count -ne 1 -or $loaded[0].name -ne $Inventory.activeBroker) {
        throw 'Exactly the active broker must be loaded.'
    }

    $endpoints = @{}
    foreach ($broker in $brokers) {
        [void](Get-RequiredProperty $broker 'name')
        $mqttUri = [string](Get-RequiredProperty $broker 'mqttUri')
        if ($endpoints.ContainsKey($mqttUri)) {
            throw "Duplicate MQTT endpoint '$mqttUri'."
        }
        $endpoints[$mqttUri] = $true

        [void](Get-RequiredProperty $broker 'image')
        $digestProperty = $broker.PSObject.Properties['digest']
        $digest = if ($null -eq $digestProperty) { '' } else { [string]$digestProperty.Value }
        $provenance = $broker.PSObject.Properties['digestProvenance']
        if ($Action -ne 'pull' -and $broker.name -eq $Inventory.activeBroker -and ($digest -notmatch '^sha256:[0-9a-f]{64}$' -or $digest -match '^sha256:(.)\1{63}$' -or
            $null -eq $provenance -or $provenance.Value.status -ne 'verified')) {
            throw "Broker '$($broker.name)' has unresolved image identity; resolve an immutable digest with verified provenance before deployment."
        }

        $cpuset = [string](Get-RequiredProperty $broker 'cpuset')
        if ($cpuMode -eq 'singleCorePinned' -and $cpuset -notmatch '^\d+$') {
            throw "Broker '$($broker.name)' must pin exactly one CPU in singleCorePinned mode."
        }
        if ($cpuMode -eq 'hostAllCores' -and -not [string]::IsNullOrWhiteSpace($cpuset)) {
            throw "Broker '$($broker.name)' must not define cpuset in hostAllCores mode."
        }

        if ([Int64](Get-RequiredProperty $broker 'memoryLimit') -ne 4294967296) {
            throw "Broker '$($broker.name)' must use the 4294967296-byte baseline memory limit."
        }
    }

    $campaign = $Inventory.PSObject.Properties['campaign']
    if ($null -ne $campaign -and $null -ne $campaign.Value) {
        if ($campaign.Value.deploymentMode -ne $deploymentMode) {
            throw 'Campaign deployment mode must match the broker stand deployment mode.'
        }
        if ($campaign.Value.cpuMode -ne $cpuMode) {
            throw 'Campaign CPU mode must match the broker stand CPU mode.'
        }
    }
}

function Get-ActiveBroker {
    param([Parameter(Mandatory = $true)]$Inventory)

    $matches = @($Inventory.brokers | Where-Object { $_.name -eq $Inventory.activeBroker })
    if ($matches.Count -ne 1) { throw "Active broker '$($Inventory.activeBroker)' is not in inventory." }
    return $matches[0]
}

function Get-DockerContext {
    param([Parameter(Mandatory = $true)]$Inventory, [Parameter(Mandatory = $true)]$Broker)

    if ($Inventory.deploymentMode -eq 'distributedHosts') {
        return [string](Get-RequiredProperty $Broker 'dockerContext')
    }
    return [string](Get-RequiredProperty $Inventory 'dockerContext')
}

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = & $DockerExecutable @Arguments 2>&1
    $exitCode = if (Test-Path Variable:LASTEXITCODE) { $LASTEXITCODE } else { 0 }
    if ($exitCode -ne 0) {
        throw "Docker command failed: $($Arguments -join ' ')"
    }

    return ($result | Out-String).Trim()
}

function Get-ComposePath {
    foreach ($candidate in @((Join-Path $PSScriptRoot 'docker\brokers\compose.yml'), (Join-Path $PSScriptRoot '..\docker\brokers\compose.yml'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Broker compose file was not found.'
}

function New-EffectiveComposeOverride {
    param([Parameter(Mandatory = $true)]$Inventory, [Parameter(Mandatory = $true)]$Broker)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $port = if ($Inventory.deploymentMode -eq 'singleHostSequential') { $Broker.singleHostMqttPort } else { $Broker.distributedMqttPort }
    $image = "$($Broker.image)@$($Broker.digest)"
    $lines = @('services:', "  $($Broker.serviceName):", "    image: $image", '    network_mode: host', '    mem_limit: 4g', '    environment:', "      - MQTT_PORT=$port", "      - CONTROL_INTERFACE=$($Inventory.controlInterface)")
    if ($Inventory.cpuMode -eq 'singleCorePinned') { $lines += "    cpuset: `"$($Broker.cpuset)`"" }
    if ($Broker.name -eq 'Mosquitto') {
        $brokerConfig = Join-Path $OutputDirectory 'mosquitto.conf'
        @("listener $port 0.0.0.0", 'allow_anonymous true', 'persistence false', 'log_dest stdout', 'log_type warning', 'connection_messages false') | Set-Content -LiteralPath $brokerConfig
        $lines += @('    volumes:', "      - `"$brokerConfig`:/mosquitto/config/mosquitto.conf:ro`"")
    }
    if ($Broker.name -eq 'EMQX') {
        $brokerConfig = Join-Path $OutputDirectory 'emqx.conf'
        @("listeners.tcp.default.bind = `"0.0.0.0:$port`"", "dashboard.listeners.http.bind = `"$($Inventory.controlInterface):18083`"", 'durable_sessions.enable = false', 'log.console.level = warning') | Set-Content -LiteralPath $brokerConfig
        $lines += @('    volumes:', "      - `"$brokerConfig`:/opt/emqx/etc/emqx.conf:ro`"")
    }
    $path = Join-Path $OutputDirectory 'effective-compose.yml'
    Set-Content -LiteralPath $path -Value $lines
    return $path
}

function Stop-OtherStandContainers {
    param([Parameter(Mandatory = $true)]$Inventory, [Parameter(Mandatory = $true)]$Broker)

    if ($Inventory.deploymentMode -ne 'singleHostSequential') { return }
    $context = Get-DockerContext $Inventory $Broker
    $names = Invoke-Docker @('--context', $context, 'ps', '-a', '--filter', 'label=mqttbenchmark.broker=true', '--format', '{{.Names}}')
    foreach ($name in @($names -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        if ($name -ne $Broker.containerName) {
            Invoke-Docker @('--context', $context, 'rm', '-f', $name) | Out-Null
        }
    }
}

function Test-MqttReadiness {
    param([Parameter(Mandatory = $true)][string]$MqttUri)

    $uri = [Uri]$MqttUri
    if ($uri.Scheme -ne 'mqtt') {
        throw "MQTT readiness requires an mqtt URI, got '$MqttUri'."
    }

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        try {
            $connect = $client.ConnectAsync($uri.Host, $uri.Port)
            if (-not $connect.Wait(5000)) {
                throw "MQTT readiness timed out while connecting to '$MqttUri'."
            }
        }
        catch {
            throw "MQTT readiness failed while connecting to '$MqttUri'."
        }

        $stream = $client.GetStream()
        $packet = [byte[]](0x10, 0x12, 0x00, 0x04, 0x4d, 0x51, 0x54, 0x54, 0x04, 0x02, 0x00, 0x05, 0x00, 0x06, 0x68, 0x65, 0x61, 0x6c, 0x74, 0x68)
        $stream.Write($packet, 0, $packet.Length)
        $stream.ReadTimeout = 5000
        $response = New-Object byte[] 4
        $offset = 0
        while ($offset -lt $response.Length) {
            $count = $stream.Read($response, $offset, $response.Length - $offset)
            if ($count -eq 0) { throw "MQTT readiness received an incomplete CONNACK from '$MqttUri'." }
            $offset += $count
        }

        if ($response[0] -ne 0x20 -or $response[1] -ne 0x02 -or $response[2] -ne 0x00 -or $response[3] -ne 0x00) {
            throw "MQTT readiness received a non-success CONNACK from '$MqttUri'."
        }
    }
    finally {
        $client.Dispose()
    }
}

function Write-Capture {
    param(
        [Parameter(Mandatory = $true)]$Inventory,
        [Parameter(Mandatory = $true)]$Broker
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $logsDirectory = Join-Path $OutputDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null

    $context = Get-DockerContext $Inventory $Broker
    $container = [string](Get-RequiredProperty $Broker 'containerName')
    $version = Invoke-Docker @('--context', $context, 'version', '--format', '{{json .}}')
    $imageIdentity = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{json .}}', "$($Broker.image)@$($Broker.digest)")
    [ordered]@{ docker = $version; image = $imageIdentity; requestedImage = "$($Broker.image)@$($Broker.digest)" } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'versions.json') -NoNewline

    Start-Sleep -Seconds 1
    $stats = Invoke-Docker @('--context', $context, 'stats', '--no-stream', '--format', '{{json .}}', $container)
    $throttling = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/sys/fs/cgroup/cpu.stat')
    $rss = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/sys/fs/cgroup/memory.current')
    $network = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/proc/net/dev')
    $diskIo = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/sys/fs/cgroup/io.stat')
    $connections = Invoke-Docker @('--context', $context, 'exec', $container, 'ss', '-tan')
    [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        container = $container
        dockerStats = $stats
        cgroupCpuAndThrottle = $throttling
        cgroupRssBytes = $rss
        network = $network
        networkScope = 'host-shared'
        diskIo = $diskIo
        connections = $connections
        connectionsScope = 'host-shared'
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $OutputDirectory 'telemetry.ndjson')

    $logs = Invoke-Docker @('--context', $context, 'logs', '--timestamps', $container)
    Set-Content -LiteralPath (Join-Path $logsDirectory "$($Broker.name).log") -Value $logs -NoNewline

    $hashes = @()
    $effectiveOverride = New-EffectiveComposeOverride $Inventory $Broker
    $inputFiles = @($ConfigPath, (Get-ComposePath), $effectiveOverride) +
        @(Get-ChildItem -LiteralPath (Split-Path -Parent (Get-ComposePath)) -Recurse -File | Select-Object -ExpandProperty FullName)
    foreach ($path in $inputFiles | Sort-Object -Unique) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $hashes += [ordered]@{
                path = [IO.Path]::GetFullPath($path)
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
            }
        }
    }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'config-hashes.json') -NoNewline
}

try {
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Configuration file '$ConfigPath' was not found."
    }

    $inventory = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
    Test-BrokerStandInventory $inventory

    $activeBroker = Get-ActiveBroker $inventory
    $context = Get-DockerContext $inventory $activeBroker
    switch ($Action) {
        'validate' { Write-ControllerResult -Status 'valid' -Message 'Broker stand inventory is valid.'; break }
        'pull' {
            if ($activeBroker.name -in @('Aedes', 'ActiveMQ')) {
                $service = [string](Get-RequiredProperty $activeBroker 'serviceName')
                Invoke-Docker @('--context', $context, 'compose', '-f', (Get-ComposePath), 'build', $service) | Out-Null
                New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
                $imageId = Invoke-Docker @('--context', $context, 'image', 'inspect', '--format', '{{.Id}}', [string]$activeBroker.image)
                [ordered]@{ broker = $activeBroker.name; image = $activeBroker.image; imageId = $imageId; capturedAt = [DateTimeOffset]::UtcNow.ToString('O') } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-provenance.json')
            }
            else {
                Invoke-Docker @('--context', $context, 'pull', "$($activeBroker.image)@$($activeBroker.digest)") | Out-Null
            }
            Write-ControllerResult -Status 'pulled' -Message "Pulled $($activeBroker.name)."; break
        }
        'start' {
            $service = [string](Get-RequiredProperty $activeBroker 'serviceName')
            Stop-OtherStandContainers $inventory $activeBroker
            $override = New-EffectiveComposeOverride $inventory $activeBroker
            Invoke-Docker @('--context', $context, 'compose', '-f', (Get-ComposePath), '-f', $override, 'up', '-d', $service) | Out-Null
            Write-ControllerResult -Status 'started' -Message "Started $($activeBroker.name)."; break
        }
        'health' {
            $container = [string](Get-RequiredProperty $activeBroker 'containerName')
            $health = Invoke-Docker @('--context', $context, 'inspect', '--format', '{{.State.Status}}', $container)
            if ($health -ne 'running') { throw "Broker '$($activeBroker.name)' is not running." }
            Test-MqttReadiness ([string](Get-RequiredProperty $activeBroker 'mqttUri'))
            Write-ControllerResult -Status 'healthy' -Message "Broker $($activeBroker.name) accepted MQTT CONNECT."; break
        }
        'reset' {
            $container = [string](Get-RequiredProperty $activeBroker 'containerName')
            Invoke-Docker @('--context', $context, 'rm', '-f', $container) | Out-Null
            Write-ControllerResult -Status 'reset' -Message "Removed $($activeBroker.name)."; break
        }
        'capture' {
            Write-Capture $inventory $activeBroker
            Write-ControllerResult -Status 'captured' -Message "Captured $($activeBroker.name)."; break
        }
        'stop' {
            $container = [string](Get-RequiredProperty $activeBroker 'containerName')
            Invoke-Docker @('--context', $context, 'stop', $container) | Out-Null
            Write-ControllerResult -Status 'stopped' -Message "Stopped $($activeBroker.name)."; break
        }
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
