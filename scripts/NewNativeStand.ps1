[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BrokerSpecPath,
    [Parameter(Mandatory)][string]$StandPath,
    [Parameter(Mandatory)][string]$CampaignPath,
    [ValidateSet('singleCorePinned', 'hostAllCores')][string]$CpuMode = 'hostAllCores',
    [string]$CpuSet = '',
    [ValidateSet('managed', 'external')][string]$Lifecycle = 'managed',
    [string]$NativeHostPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Resolve-InputPath([string]$Value) {
    if ($Value.StartsWith('${MqttBenchmarkRoot}/') -or $Value.StartsWith('${MqttBenchmarkRoot}\')) {
        return [IO.Path]::GetFullPath((Join-Path $repo $Value.Substring('${MqttBenchmarkRoot}'.Length).TrimStart('/', '\')))
    }
    if (-not [IO.Path]::IsPathRooted($Value)) { throw "Use an absolute path or MqttBenchmarkRoot token: $Value" }
    return [IO.Path]::GetFullPath($Value)
}
function Pin-Input([string]$Value) {
    $path = Resolve-InputPath $Value
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Native input not found: $path" }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($CpuMode -eq 'singleCorePinned' -and ($CpuSet -notmatch '^\d+$' -or [int]$CpuSet -gt 63)) { throw 'singleCorePinned requires one CPU number from 0 to 63 in -CpuSet.' }
if ($CpuMode -eq 'hostAllCores' -and $CpuSet) { throw 'hostAllCores requires an empty CpuSet.' }
foreach ($path in @($StandPath, $CampaignPath)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" }
}
$spec = Get-Content -Raw -LiteralPath $BrokerSpecPath | ConvertFrom-Json -AsHashtable
$entries = @($spec.brokers)
if (-not $entries.Count) { throw 'Broker spec must contain a nonempty brokers array.' }
$names = @($entries | ForEach-Object { [string]$_.name })
if (@($names | Select-Object -Unique).Count -ne $names.Count -or @($names | Where-Object { $_ -notin @('Aedes','Mosquitto','ActiveMQ','EMQX') }).Count) { throw 'Broker names must be unique and supported.' }
$brokers = @()
$thresholds = [ordered]@{}
foreach ($entry in $entries) {
    $name = [string]$entry.name
    $endpoint = [Uri]$entry.mqttUri
    if ($endpoint.Scheme -ne 'mqtt' -or $endpoint.Port -lt 1) { throw 'Every broker requires a valid mqttUri.' }
    if ($entries.Count -gt 1) {
        $expectedPort = @{ Aedes=1883; Mosquitto=2883; ActiveMQ=3883; EMQX=4883 }[$name]
        if ($endpoint.Port -ne $expectedPort) { throw "Multi-broker singleHostSequential requires $name on port $expectedPort." }
    }
    $entry.runtime = 'native'; $entry.lifecycle = $Lifecycle
    $entry.serviceName = $name.ToLowerInvariant(); $entry.containerName = $entry.serviceName
    $entry.loaded = $name -eq $names[0]
    $entry.executable = Resolve-InputPath ([string]$entry.executable)
    $entry.executableSha256 = Pin-Input $entry.executable
    if ($name -eq 'EMQX') {
        if (-not $entry.Contains('environment')) { $entry.environment = @{} }
        if (-not $entry.environment.Contains('DEBUG')) { $entry.environment.DEBUG = '0' }
    }
    if ($entry.Contains('launcherExecutable') -and $entry.launcherExecutable) {
        $entry.launcherExecutable = Resolve-InputPath ([string]$entry.launcherExecutable)
        $entry.launcherSha256 = Pin-Input $entry.launcherExecutable
    }
    if (-not $entry.Contains('args') -or $entry.args -is [string]) { throw "Broker $name requires an args array." }
    if (-not $entry.Contains('configFiles')) { $entry.configFiles = @() }
    foreach ($file in @($entry.configFiles)) { [void](Pin-Input ([string]$file)) }
    $entry.singleHostMqttPort = $endpoint.Port; $entry.distributedMqttPort = 1883
    $entry.managementUri = $null; $entry.cpuset = $CpuSet; $entry.memoryLimit = 4294967296
    $brokers += $entry
    $thresholds[$name] = if ($endpoint.IsLoopback -or $name -eq 'Aedes') { $null } else { 80 }
}
$stand = [ordered]@{
    schemaVersion='broker-stand.v1'; deploymentMode='singleHostSequential'; cpuMode=$CpuMode
    activeBroker=$names[0]; dockerContext='native-local'; controlInterface='127.0.0.1'; networkMode='host'; brokers=$brokers
}
if ($NativeHostPath) {
    $stand.nativeHost = Get-Content -Raw -LiteralPath $NativeHostPath | ConvertFrom-Json -AsHashtable
    $stand.dockerContext = 'native-ssh-' + $stand.nativeHost.host
}
$campaign = [ordered]@{
    schemaVersion=3; seed=20260909; deploymentMode='singleHostSequential'; cpuMode=$CpuMode
    brokers=$names; customImageBrokers=@(); messageBytes=@(16,256); qos=@(0,1,2); publishers=@(1,16,64,128)
    repeats=3; warmupSeconds=5; measurementSeconds=30; cooldownSeconds=5; drainTimeoutSeconds=30; quietPeriodSeconds=2
    networkThresholdPercentByBroker=$thresholds
}
foreach ($item in @(@{path=$StandPath;value=$stand}, @{path=$CampaignPath;value=$campaign})) {
    $full = [IO.Path]::GetFullPath($item.path)
    [void][IO.Directory]::CreateDirectory((Split-Path $full))
    $stream = [IO.File]::Open($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $item.value -Depth 30)); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}
Write-Host "Native stand: $StandPath; campaign: $CampaignPath. Inputs pinned on this broker machine."
