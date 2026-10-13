[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$InputStandPaths,
    [Parameter(Mandatory)][string]$StandPath,
    [Parameter(Mandatory)][string]$CampaignPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
foreach ($path in @($StandPath,$CampaignPath)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" }
}
if ($InputStandPaths.Count -lt 2) { throw 'Supply at least two one-broker stand inventories generated on their broker hosts.' }
$entries = @(); $names = @{}; $endpoints = @{}; $cpuMode = $null
foreach ($path in $InputStandPaths) {
    $inputStand = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    if ($inputStand.schemaVersion -ne 'broker-stand.v1' -or @($inputStand.brokers).Count -ne 1) { throw 'Each input must be a one-broker stand.v1 inventory.' }
    if ($cpuMode -and $cpuMode -ne $inputStand.cpuMode) { throw 'All inputs must use the same CPU mode.' }
    $cpuMode = [string]$inputStand.cpuMode
    $entry = $inputStand.brokers[0]
    if ($entry.runtime -ne 'native') { throw 'This merge script accepts native brokers only.' }
    if ($entry.Contains('nativeHost') -and $entry.nativeHost) { $remote = $entry.nativeHost }
    elseif ($inputStand.Contains('nativeHost') -and $inputStand.nativeHost) { $remote = $inputStand.nativeHost }
    else { throw 'Each remote input must contain nativeHost.' }
    if ($entry.name -notin @('Aedes','Mosquitto','ActiveMQ','EMQX') -or $names.ContainsKey($entry.name)) { throw 'Broker names must be unique and supported.' }
    if ($endpoints.ContainsKey($entry.mqttUri)) { throw 'MQTT endpoints must be unique.' }
    $names[$entry.name]=$true; $endpoints[$entry.mqttUri]=$true
    $entry.nativeHost=$remote
    $sshPort=if ($remote.Contains('port')) { $remote.port } else { 22 }
    $entry.dockerContext="native-ssh://$($remote.user)@$($remote.host):$sshPort/$($remote.workRoot)"
    $entry.distributedMqttPort=([Uri]$entry.mqttUri).Port
    $entry.singleHostMqttPort=@{Aedes=1883;Mosquitto=2883;ActiveMQ=3883;EMQX=4883}[$entry.name]
    $entry.loaded=$entries.Count -eq 0
    $entries += $entry
}
$stand=[ordered]@{
    schemaVersion='broker-stand.v1';deploymentMode='distributedHosts';cpuMode=$cpuMode
    activeBroker=$entries[0].name;dockerContext='native-distributed';controlInterface='127.0.0.1';networkMode='host';brokers=$entries
}
$thresholds=[ordered]@{}
foreach ($entry in $entries) { $thresholds[$entry.name]=if (([Uri]$entry.mqttUri).IsLoopback -or $entry.name -eq 'Aedes') { $null } else { 80 } }
$campaign=[ordered]@{
    schemaVersion=3;seed=20260909;deploymentMode='distributedHosts';cpuMode=$cpuMode
    brokers=@($entries | ForEach-Object {$_.name});customImageBrokers=@();messageBytes=@(16,256);qos=@(0,1,2);publishers=@(1,16,64,128)
    repeats=3;warmupSeconds=5;measurementSeconds=30;cooldownSeconds=5;drainTimeoutSeconds=30;quietPeriodSeconds=2
    networkThresholdPercentByBroker=$thresholds
}
foreach ($item in @(@{path=$StandPath;value=$stand},@{path=$CampaignPath;value=$campaign})) {
    $full=[IO.Path]::GetFullPath($item.path)
    [void][IO.Directory]::CreateDirectory((Split-Path $full))
    $stream=[IO.File]::Open($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $item.value -Depth 40));$stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
}
Write-Host 'Distributed native stand and campaign created. Review every target endpoint/path before preflight.'
