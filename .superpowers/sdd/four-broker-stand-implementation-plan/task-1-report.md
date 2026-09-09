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
- Initial registry lookup did not resolve the requested tags. This is superseded by the verified Mosquitto/EMQX digests and the reproducible ActiveMQ build documented in fix round 1; no fabricated digest remains.

## Fix round 1

- Reworked Aedes startup for the Aedes 1.x named `createBroker` API, awaited listener readiness, and emitted a readiness event.  The Node 20 base remains digest-pinned.
- Replaced synthetic image digests with the verified Mosquitto 2.1.2-alpine and EMQX 6.3.0 multi-arch digests.  ActiveMQ Classic 6.3.2 is now a reproducible custom image on the verified Temurin 21 JRE Alpine base; its official tarball SHA-512 is verified during build.  Build-derived Aedes/ActiveMQ identities remain explicitly unresolved until a target build records their image IDs; they are never represented as fabricated digests.
- Adopted `singleHostSequential`/`distributedHosts`, the 4 GiB (`4294967296`) inventory baseline, direct host networking, generated effective Compose overrides, and single-host reconciliation of labeled inactive containers.
- Controller health now requires a bounded MQTT 3.1.1 CONNECT/CONNACK round trip.  Capture appends one-second NDJSON samples with Docker stats, cgroup CPU/throttle/RSS, network, disk I/O, connections, image identity, logs, and hashes of all effective broker inputs.
- Replaced the generic Docker shim with command-specific responses and assertions covering effective host-network configuration, lifecycle cleanup, capture content, and image/version artifacts.
- Added `InfraConfigEnvironmentResolver`: `${NAME}` references are expanded only from the process environment, an unset name fails without echoing a value, and NBomber consumes the resolved runtime file.

### Fix-round RED/GREEN evidence

- RED: health reported success for a running container with an unreachable endpoint; GREEN: health fails unless a configured endpoint completes MQTT CONNECT/CONNACK.
- RED: generated effective Compose output contained published ports; GREEN: focused controller tests assert `network_mode: host`, listener environment, and no `ports:` stanza.
- RED: resolver test did not compile before the resolver existed; GREEN: `InfraConfigResolver_ExpandsEnvironmentReferenceOrFailsWithoutEchoingValue` passes.

### Fix-round verification and remaining concern

- Focused controller/resolver suite: 9 passed, 0 failed.
- The local host provides Node 18 only, so `npm ci --dry-run --ignore-scripts` validates lockfile resolution with expected Node>=20 engine warnings; target-image verification requires the Node 20 Docker build.
- Docker and outbound 443 are unavailable locally.  No remote shell or credential access was attempted.  The remote controller run must verify image pulls/builds, host-network listener bindings, and broker readiness through the arranged tunnel.

## Fix round 2

- Corrected Aedes 1.1.2 startup to use `Aedes.createBroker()` and regenerated `package-lock.json` with Node 20 against the npm registry. A Node 20 `npm ci --dry-run --ignore-scripts` clean resolution completed with 0 vulnerabilities.
- Made the base Compose file render without mandatory image variables while retaining direct Linux host networking and no published ports. The effective override now drives host-network mode, MQTT listener environment, CPU pinning, memory limit, and concrete per-run Mosquitto/EMQX configurations.
- Active broker selection is now based on `activeBroker` and requires exactly that inventory entry to be loaded; distributed mode selects the active broker's Docker context. Aedes and ActiveMQ both take the custom build branch and record their local immutable image ID in a build-provenance artifact.
- Capture no longer reads `/proc/self/io`; host-network network and connection fields are marked host-shared. The environment resolver now parses JSON nodes before replacement, so quotes, backslashes, and newlines are JSON-escaped rather than text-injected.

### Fix-round verification

- `npx --yes node@20 <npm-cli> install --package-lock-only --ignore-scripts` followed by `ci --dry-run --ignore-scripts`: success, 27 packages, 0 vulnerabilities.
- `dotnet test src\EntityFX.MqttBenchmark.sln --no-restore`: 19 passed, 0 failed, 0 skipped. Existing unrelated nullable warning in `Benchmark.cs:268` remains.
- `git diff --check`: no whitespace errors (only repository CRLF conversion notices).

### Remaining remote verification

- Local Docker remains unavailable. The remote controller must render Compose, build both custom images, record their image IDs, validate actual host listener/control-interface bindings, and exercise multi-sample capture under the temporary outbound tunnel.
