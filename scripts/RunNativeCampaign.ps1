[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$StandPath,
    [Parameter(Mandatory)][string]$CampaignDirectory,
    [string]$BenchmarkRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$AggregatedDirectory,
    [switch]$Pilot,
    [switch]$Resume,
    [string]$Broker
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root 'bin/EntityFX.MqttBenchmark.Cli.dll'
# Preserve array shape even for a single published DLL; scalar string splatting
# otherwise passes its first character as dotnet's command.
$prefix = @(if (Test-Path $cli) { $cli } else { 'run'; '-c'; 'Release'; '--project'; (Join-Path $root 'src/EntityFX.MqttBenchmark.Cli'); '--' })
$common = @('--config',([IO.Path]::GetFullPath($ConfigPath)),'--stand',([IO.Path]::GetFullPath($StandPath)),
    '--benchmark-repo',([IO.Path]::GetFullPath($BenchmarkRepository)),'--stand-script',(Join-Path $PSScriptRoot 'BrokerStand.ps1'))
$campaign = [IO.Path]::GetFullPath($CampaignDirectory)
$preflight = $campaign + '-preflight-' + [Guid]::NewGuid().ToString('N')
function Invoke-Benchmark([string[]]$Options) {
    & dotnet @prefix @Options
    if ($LASTEXITCODE -ne 0) { throw "Benchmark command '$($Options[0])' failed with exit code $LASTEXITCODE." }
}
$selection = if ($Broker) { @('--broker',$Broker) } else { @() }
Invoke-Benchmark (@('preflight') + $common + @('--output',$preflight) + $selection)
$run = @('matrix') + $common + @('--campaign',$campaign) + $selection
if ($Pilot) { $run += @('--max-runs','1') }
if ($Resume) { $run += '--resume' }
Invoke-Benchmark $run
if (-not $Pilot) {
    $aggregate = if ($AggregatedDirectory) { [IO.Path]::GetFullPath($AggregatedDirectory) } else { $campaign + '-aggregate' }
    Invoke-Benchmark (@('aggregate') + $common + @('--raw',$campaign,'--output',$aggregate))
} else { Write-Host 'Pilot finished. A pilot is not a complete calibration dataset.' }
