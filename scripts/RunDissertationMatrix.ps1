param(
    [string]$Matrix = "config/benchmark-matrix.v2.json",
    [string]$RawRoot = "results/raw",
    [Parameter(Mandatory = $true)]
    [string]$Campaign
)

$ErrorActionPreference = "Stop"
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
    --config $Matrix `
    --output $RawRoot `
    --campaign $Campaign

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
