[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StandPath,
    [Parameter(Mandatory)][string]$Executable
)

# One-shot operator setup: pins the SHA-256 of a native broker executable into the
# stand inventory so that every later run verifies the exact binary identity.
# Пример:
#   pwsh scripts/PinNativeExecutable.ps1 -StandPath config/broker-stand.native-mosquitto.json `
#       -Executable C:/mosquitto/mosquitto.exe

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$StandPath = [IO.Path]::GetFullPath($StandPath)
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Executable not found: $Executable" }
if (-not (Test-Path -LiteralPath $StandPath -PathType Leaf)) { throw "Stand inventory not found: $StandPath" }

$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $Executable).Hash.ToLowerInvariant()
$inventory = Get-Content -Raw -LiteralPath $StandPath | ConvertFrom-Json -AsHashtable
$updated = 0
foreach ($broker in $inventory.brokers) {
    if ($broker.name -eq 'Mosquitto' -or (-not $broker.Contains('runtime')) -or $broker.runtime -eq 'native') {
        if (-not $broker.Contains('executable')) { continue }
        if ([IO.Path]::GetFullPath([string]$broker.executable) -ne $Executable) { continue }
        $broker.executableSha256 = $sha
        $updated++
    }
}
if ($updated -eq 0) { throw "No native broker with executable '$Executable' found in the inventory." }
$inventory | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $StandPath -Encoding utf8NoBOM
Write-Host "Pinned $sha for $Executable ($updated broker(s)) in $StandPath"