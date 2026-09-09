[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('validate', 'pull', 'start', 'health', 'reset', 'capture', 'stop')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [string]$OutputDirectory = 'artifacts/broker-stand',

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
    if ($deploymentMode -notin @('singleHost', 'distributed')) {
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
    if ($deploymentMode -eq 'singleHost' -and $loaded.Count -ne 1) {
        throw 'Single-host mode requires exactly one loaded broker.'
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
        $digest = [string](Get-RequiredProperty $broker 'digest')
        if ($digest -notmatch '^sha256:[0-9a-f]{64}$') {
            throw "Broker '$($broker.name)' must have an immutable sha256 digest."
        }

        $cpuset = [string](Get-RequiredProperty $broker 'cpuset')
        if ($cpuMode -eq 'singleCorePinned' -and $cpuset -notmatch '^\d+$') {
            throw "Broker '$($broker.name)' must pin exactly one CPU in singleCorePinned mode."
        }

        [void](Get-RequiredProperty $broker 'memoryLimit')
    }

    if ($deploymentMode -eq 'singleHost' -and $loaded[0].name -ne $Inventory.activeBroker) {
        throw 'The active broker must be the single loaded broker.'
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

    return @($Inventory.brokers | Where-Object { $_.loaded -eq $true })[0]
}

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = & $DockerExecutable @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed: $($Arguments -join ' ')"
    }

    return ($result | Out-String).Trim()
}

function Get-ComposePath {
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\docker\brokers\compose.yml'))
}

function Write-Capture {
    param(
        [Parameter(Mandatory = $true)]$Inventory,
        [Parameter(Mandatory = $true)]$Broker
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $logsDirectory = Join-Path $OutputDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null

    $context = [string]$Inventory.dockerContext
    $container = [string](Get-RequiredProperty $Broker 'containerName')
    $version = Invoke-Docker @('--context', $context, 'version', '--format', '{{json .}}')
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'versions.json') -Value $version -NoNewline

    Start-Sleep -Seconds 1
    $stats = Invoke-Docker @('--context', $context, 'stats', '--no-stream', '--format', '{{json .}}', $container)
    $throttling = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/sys/fs/cgroup/cpu.stat')
    $network = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/proc/net/dev')
    $diskIo = Invoke-Docker @('--context', $context, 'exec', $container, 'cat', '/proc/self/io')
    $connections = Invoke-Docker @('--context', $context, 'exec', $container, 'ss', '-tan')
    [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        container = $container
        cpuAndRss = $stats
        cpuThrottling = $throttling
        network = $network
        diskIo = $diskIo
        connections = $connections
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $OutputDirectory 'telemetry.ndjson') -NoNewline

    $logs = Invoke-Docker @('--context', $context, 'logs', '--timestamps', $container)
    Set-Content -LiteralPath (Join-Path $logsDirectory "$($Broker.name).log") -Value $logs -NoNewline

    $hashes = @()
    foreach ($path in @($ConfigPath, (Get-ComposePath))) {
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
    $context = [string]$inventory.dockerContext
    switch ($Action) {
        'validate' { Write-ControllerResult -Status 'valid' -Message 'Broker stand inventory is valid.'; break }
        'pull' {
            if ($activeBroker.name -eq 'Aedes') {
                $service = [string](Get-RequiredProperty $activeBroker 'serviceName')
                Invoke-Docker @('--context', $context, 'compose', '-f', (Get-ComposePath), 'build', $service) | Out-Null
            }
            else {
                Invoke-Docker @('--context', $context, 'pull', "$($activeBroker.image)@$($activeBroker.digest)") | Out-Null
            }
            Write-ControllerResult -Status 'pulled' -Message "Pulled $($activeBroker.name)."; break
        }
        'start' {
            $service = [string](Get-RequiredProperty $activeBroker 'serviceName')
            Invoke-Docker @('--context', $context, 'compose', '-f', (Get-ComposePath), 'up', '-d', $service) | Out-Null
            Write-ControllerResult -Status 'started' -Message "Started $($activeBroker.name)."; break
        }
        'health' {
            $container = [string](Get-RequiredProperty $activeBroker 'containerName')
            $health = Invoke-Docker @('--context', $context, 'inspect', '--format', '{{.State.Status}}', $container)
            if ($health -ne 'running') { throw "Broker '$($activeBroker.name)' is not running." }
            Write-ControllerResult -Status 'healthy' -Message "Broker $($activeBroker.name) is running."; break
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
