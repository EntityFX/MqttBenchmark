# Task 1 report: reproducible broker stand and controller

## Implementation

- Added `docker/brokers/compose.yml` with mutually exclusive profiles for Aedes, Mosquitto, ActiveMQ Classic, and EMQX; single-host MQTT ports are 1883, 2883, 3883, and 4883.  The two management ports are bound through `CONTROL_INTERFACE`, defaulting to `127.0.0.1`.
- Added explicit broker configurations with persistence disabled, `unless-stopped` restart policy, 512 MiB memory limit, CPU set `0`, and bounded local logging.  Aedes is built from Node 20 Alpine pinned by digest, uses Aedes 1.1.2, and has a committed lockfile.
- Added `config/broker-stand.v1.json` and `scripts/BrokerStand.ps1`.  The controller implements `validate`, `pull`, `start`, `health`, `reset`, `capture`, and `stop`; validates inventory/campaign compatibility; and captures a one-second telemetry sample, logs, version data, and SHA-256 configuration hashes.
- Replaced the Bomber infrastructure credentials with environment-variable references.  No prior credential material is reproduced here.

## Test-first evidence

- RED: `dotnet test src\EntityFX.MqttBenchmark.Tests\EntityFX.MqttBenchmark.Tests.csproj --filter FullyQualifiedName~BrokerStandControllerTests` failed because `BrokerStand.ps1` did not exist (exit 64).
- GREEN: the same command passed `1/1` after the minimal validation implementation.
- RED: after adding campaign/capture/reset contracts, the focused command failed `3/4`: incompatible campaign was accepted and `DockerExecutable` was unsupported.
- GREEN: the focused command passed `4/4` after compatibility validation and controlled-Docker actions were implemented.
- RED: the Aedes pull-path contract failed because it attempted `pull` rather than local Compose `build`.
- GREEN: the focused command passed `7/7` after the Aedes build path was implemented.

## Verification

- `pwsh -NoProfile -File scripts\BrokerStand.ps1 -Action validate -ConfigPath config\broker-stand.v1.json` returned `{"action":"validate","status":"valid",...}`.
- `npm ci --dry-run --ignore-scripts` in `docker/brokers/aedes` resolved 27 packages.  The host Node 18 emitted expected engine warnings; the Docker image uses Node 20.
- `dotnet test src\EntityFX.MqttBenchmark.sln --no-restore` passed: 17 passed, 0 failed, 0 skipped.
- `git diff --check` returned no whitespace errors (Git emitted only pre-existing line-ending conversion warnings).

## Self-review and concerns

- Controller tests invoke the real PowerShell controller with a disposable Docker command shim, so they do not require a Docker CLI or a live container.
- The host has no Docker CLI, so Compose parsing, image pulls, startup, health probes, and real container telemetry were not run.
- Public Docker Hub lookup did not resolve the requested Mosquitto 2.1.2 or ActiveMQ Classic 6.3.2 tags at implementation time. Their manifest digests in the stand are syntactically pinned placeholders and must be replaced with verified registry digests before a real deployment; no real deployment is claimed.
