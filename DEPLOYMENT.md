# MqttBenchmark — Deploying the portable package

This document describes how to deploy and run the four-broker calibration stand
(campaign schema **v3**) from a portable package.

## 1. Prerequisites

### Container profile
- **Docker** with the build context available — required for the broker images
  (aedes, mosquitto, activemq, emqx) and telemetry.
- **Windows or Linux**.
- **.NET 6.0** runtime (the CLI is framework-dependent by default).

### Native (no Docker) profile
Docker is **not** required. The broker runs as an ordinary local process, managed by the same
stand controller. This profile is the supported way to run benchmarks on Linux and works
identically on Windows.
- **.NET 6.0** runtime.
- **PowerShell 7 (`pwsh`)** on **both** platforms — the stand controller is a PowerShell script.
  On Linux install it from https://aka.ms/powershell or your distribution; if `pwsh` is not on
  `PATH`, point `MQB_STAND_SHELL` at the executable (see §3c).
- **A local broker executable**: Mosquitto, Aedes (Node.js), or any MQTT broker the stand can
  start and identify by port.

## 2. Build the portable package (machine with .NET SDK)
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

## 3a. Native profile — run a broker locally, without Docker

The same commands work; only the stand inventory differs. A native broker declares
`"runtime": "native"`, an absolute `executable` pinned by `executableSha256`, and the
`args` used to start it.

Two portable path tokens may be used in `executable`, `configFiles` and `args`:

| Token | Resolves to |
| --- | --- |
| `${MqttBenchmarkRoot}` | the repository root (the parent of `scripts/`) |
| `~/...` | the invoking user's home directory |

A token is expanded **only** when it prefixes the value, so opaque arguments such as a
port number (`"1883"`) are passed through unchanged.

**Aedes via Node.js** — no broker installation, no Docker:

```bash
# 1. Install the broker dependencies once (aedes is pinned in the committed lockfile).
npm ci --prefix docker/brokers/aedes

# 2. Pin the broker executable identity into the inventory.
pwsh scripts/PinNativeExecutable.ps1 \
  -StandPath   config/broker-stand.native.json \
  -Executable  "$(command -v node)"

# 3. Point the inventory at the broker entry point and its port, then run as usual.
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix \
  --config   config/benchmark-campaign.native.json \
  --stand    config/broker-stand.native.json \
  --campaign results/campaign-local \
  --benchmark-repo .
```

`docker/brokers/aedes/server.js` accepts the MQTT port either as the first CLI argument
(native profile) or as the `MQTT_PORT` environment variable (container profile), so the
same broker entry point serves both.

**Notes**
- On Linux the broker is pinned to one CPU and, where cgroup v2 is available, given a
  4 GiB memory limit by the controller.
- The load-generator guard on a loopback broker has no physical link capacity, so the
  campaign **must** set that broker's network threshold to `null` (informational mode).
  A non-null threshold fails closed by design.
- The repository must have no tracked changes when a campaign starts: the recorded commit
  SHA is the provenance of the measurement.

## 3b. `pwsh` on Linux

If PowerShell 7 is not on `PATH`, point the stand at it explicitly:

```bash
export MQB_STAND_SHELL=/path/to/pwsh
```

## 4. Tests
- **Unit tests** (always green, no Docker — integration tests are skipped by default):
  ```powershell
  dotnet test src\EntityFX.MqttBenchmark.Tests --nologo
  ```
  Set `MQB_STAND_SHELL` when `pwsh` is not on `PATH`.
- **Integration tests** (require `MQB_ENABLE_INTEGRATION`; the native profile additionally
  needs a broker — see §3a — and no Docker):
  ```powershell
  $env:MQB_ENABLE_INTEGRATION = '1'
  dotnet test src\EntityFX.MqttBenchmark.Tests --nologo
  ```
  `MQB_NATIVE_BROKER_EXE` + `MQB_NATIVE_BROKER_ENTRY` select an arbitrary broker
  (for example `node` + `docker/brokers/aedes/server.js`); `MQB_NATIVE_NODE_MODULES`
  points at the broker's `node_modules`. With neither set, `mosquitto` is taken from
  `PATH` or `MQB_NATIVE_MOSQUITTO_EXE`.

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