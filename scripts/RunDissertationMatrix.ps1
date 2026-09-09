param(
    [string]$Matrix = "config/benchmark-matrix.v2.json",
    [string]$RawRoot = "results/raw",
    [string]$AggregatedRoot = "results/aggregated",
    [string]$MqttYRepository = "../mqtty",
    [switch]$DryRun,
    [Parameter(Mandatory = $true)]
    [string]$Campaign
)

$ErrorActionPreference = "Stop"
if ($DryRun) {
    dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
        --config $Matrix `
        --output $RawRoot `
        --dry-run
    exit $LASTEXITCODE
}

dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
    --config $Matrix `
    --output $RawRoot `
    --campaign $Campaign

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$rawCampaign = Join-Path $RawRoot $Campaign
$aggregatedCampaign = Join-Path $AggregatedRoot $Campaign
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- aggregate `
    --config $Matrix `
    --raw $rawCampaign `
    --output $aggregatedCampaign `
    --mqtt-y-repo $MqttYRepository `
    --benchmark-repo .

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
