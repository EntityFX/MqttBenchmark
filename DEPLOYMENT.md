# MqttBenchmark — Deploying the portable package

This document describes how to deploy and run the four-broker calibration stand
(campaign schema **v3**) from a portable package.

## 1. Prerequisites
- **Windows** (the stand controller, `BrokerStand.ps1`, is PowerShell-based).
- **.NET 6.0** runtime (the CLI is framework-dependent by default).
- **Docker** with the build context available — required for the broker images
  (aedes, mosquitto, activemq, emqx) and telemetry.
- **PowerShell 5.1+**.

## 2. Build the portable package (machine with .NET SDK + Docker)
```powershell
.\scripts\BuildPortable.ps1            # -> dist\mqtt-benchmark\
```
Package layout:
```
mqtt-benchmark\
  bin\          # published CLI (EntityFX.MqttBenchmark.Cli.dll + dependencies)
  scripts\      # BrokerStand.ps1 (stand controller)
  config\       # benchmark-campaign.v3.json, broker-stand.v1.json
  docker\       # broker image definitions (aedes, mosquitto, activemq, emqx)
  DEPLOYMENT.md # this file
```

## 3. Run the campaign (from the package root)
```powershell
# a) Preview the matrix (no brokers, no Docker needed).
.\bin\EntityFX.MqttBenchmark.Cli.dll matrix --dry-run `
  --config config\benchmark-campaign.v3.json `
  --stand  config\broker-stand.v1.json

# b) Preflight each broker (starts containers, checks readiness + clock alignment).
.\bin\EntityFX.MqttBenchmark.Cli.dll preflight `
  --config config\benchmark-campaign.v3.json `
  --stand  config\broker-stand.v1.json `
  --output results\preflight

# c) Run the full matrix (288 keys by default).
.\bin\EntityFX.MqttBenchmark.Cli.dll matrix `
  --config   config\benchmark-campaign.v3.json `
  --stand    config\broker-stand.v1.json `
  --campaign results\campaign-20260911 `
  --max-attempts 5

# d) Aggregate a sealed campaign.
.\bin\EntityFX.MqttBenchmark.Cli.dll aggregate `
  --config config\benchmark-campaign.v3.json `
  --raw    results\campaign-20260911 `
  --output results\aggregated
```
Stand options also available: `--docker-executable <path>`, `--benchmark-repo <dir>`,
`--stand-script <path>`, `--trusted-build-provenance <dir>`.

## 4. Tests
- **Unit tests** (always green, no Docker — integration tests are skipped by default):
  ```powershell
  dotnet test src\EntityFX.MqttBenchmark.Tests --nologo
  ```
- **Integration tests** (require Docker + broker images):
  ```powershell
  $env:MQB_ENABLE_INTEGRATION = '1'
  dotnet test src\EntityFX.MqttBenchmark.Tests --nologo
  ```

## 5. Configuration
- **Campaign matrix + limits** live in `config/benchmark-campaign.v3.json` (brokers,
  message sizes, QoS, publisher counts, repeats, durations, network/CPU gates).
  Validation is **structural** (non-empty, in-range), so the experiment count is fully
  config-driven — no hardcoded 288-key matrix.
- **Stand endpoints + images** live in `config/broker-stand.v1.json`.
- **Retry budget** per key is `--max-attempts <n>` (default 3).
- **Clock-alignment bound** is a stand-level parameter of `BrokerStand.ps1`
  (`-ClockAlignmentBoundMs`, default 3000 ms).

## 6. Troubleshooting
- `MQTT readiness timed out` — broker image failed to start; inspect `docker logs <container>`.
- `clock alignment unready` — inspect `clock-alignment.json` (offset + uncertainty); only widen
  `-ClockAlignmentBoundMs` with a documented justification.
- `--max-attempts must be a positive integer` — pass a positive integer (e.g. `--max-attempts 5`).