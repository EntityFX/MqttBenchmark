#Requires -Version 5.1
<#
.SYNOPSIS
    Assembles a portable MqttBenchmark deployment package (CLI + stand script + config + docker
    broker definitions + DEPLOYMENT.md) under dist\mqtt-benchmark.
.EXAMPLE
    .\scripts\BuildPortable.ps1                 # publish CLI + assemble the package
    .\scripts\BuildPortable.ps1 -SkipPublish    # assemble only (CLI already built)
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot) 'dist\mqtt-benchmark'),
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'

$repo       = Split-Path $PSScriptRoot
$cliProj    = Join-Path $repo 'src\EntityFX.MqttBenchmark.Cli\EntityFX.MqttBenchmark.Cli.csproj'
$binDir     = Join-Path $OutputRoot 'bin'
$scriptsDir = Join-Path $OutputRoot 'scripts'
$configDir  = Join-Path $OutputRoot 'config'
$dockerDir  = Join-Path $OutputRoot 'docker'

function Copy-Into([string]$src, [string]$dstDir) {
    if (-not (Test-Path $src)) { throw "Required source not found: $src" }
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    Copy-Item -Path $src -Destination $dstDir -Recurse -Force
}

if ($SkipPublish) {
    Write-Host "[1/4] Skipping CLI publish (-SkipPublish)." -ForegroundColor DarkGray
}
else {
    Write-Host "[1/4] Publishing CLI ($Configuration) -> $binDir"
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    & dotnet publish $cliProj -c $Configuration -o $binDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with code $LASTEXITCODE" }
}

Write-Host "[2/4] Stand controller script -> $scriptsDir"
Copy-Into (Join-Path $repo 'scripts\BrokerStand.ps1') $scriptsDir

Write-Host "[3/4] Campaign + stand config and docker broker definitions"
Copy-Into (Join-Path $repo 'config\benchmark-campaign.v3.json') $configDir
Copy-Into (Join-Path $repo 'config\broker-stand.v1.json') $configDir
Copy-Into (Join-Path $repo 'docker\brokers') $dockerDir

Write-Host "[4/4] Deployment instructions -> $OutputRoot"
Copy-Into (Join-Path $repo 'DEPLOYMENT.md') $OutputRoot

Write-Host "Portable package ready: $OutputRoot" -ForegroundColor Green