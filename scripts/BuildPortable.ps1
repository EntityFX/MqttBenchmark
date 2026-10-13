#Requires -Version 5.1
<#
.SYNOPSIS
    Assembles a portable native MqttBenchmark package (CLI, worker scripts, v3 config,
    tracked broker assets and documentation) under dist\mqtt-benchmark.
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

# Reusing an assembled directory would retain removed guides and obsolete broker assets.
# SkipPublish accepts only a pre-published bin directory; the package itself is always new.
if (Test-Path -LiteralPath $OutputRoot) {
    if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) { throw 'OutputRoot must be a directory.' }
    $existing = @(Get-ChildItem -LiteralPath $OutputRoot -Force)
    if ($existing.Count -and (-not $SkipPublish -or @($existing | Where-Object { $_.Name -ne 'bin' -or -not $_.PSIsContainer }).Count)) {
        throw 'OutputRoot contains an existing package. Choose a new output directory; obsolete files must not enter the new package.'
    }
}

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
Copy-Into (Join-Path $repo 'scripts\RemoteNativeStand.ps1') $scriptsDir
Copy-Into (Join-Path $repo 'scripts\PinNativeExecutable.ps1') $scriptsDir
Copy-Into (Join-Path $repo 'scripts\NewNativeStand.ps1') $scriptsDir
Copy-Into (Join-Path $repo 'scripts\MergeNativeStands.ps1') $scriptsDir
Copy-Into (Join-Path $repo 'scripts\RunNativeCampaign.ps1') $scriptsDir

Write-Host "[3/4] V3 campaign, tracked broker assets and documentation"
Copy-Into (Join-Path $repo 'config\benchmark-campaign.v3.json') $configDir
$assets = @(& git -C $repo ls-files -- docker/brokers)
if ($LASTEXITCODE -ne 0 -or -not $assets.Count) { throw 'Cannot enumerate tracked broker assets.' }
foreach ($asset in $assets) {
    $target = Join-Path $OutputRoot $asset
    Copy-Into (Join-Path $repo $asset) (Split-Path $target)
}
Copy-Into (Join-Path $repo 'docs') $OutputRoot

Write-Host "[4/4] Deployment instructions -> $OutputRoot"
Copy-Into (Join-Path $repo 'DEPLOYMENT.md') $OutputRoot

Write-Host "Portable package ready: $OutputRoot" -ForegroundColor Green
