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

## Fix round 3

- Custom image tags are now identical between inventory and Compose. `pull` builds both Aedes and ActiveMQ, inspects the local tag, and records its immutable local image ID plus hashes of every build input. `start` accepts an unresolved build identity only when its recorded artifact matches the selected image and current local image ID; it never forms an invalid `repo@local-image-id` reference.
- Removed inherited base CPU pinning. `hostAllCores` accepts empty/null CPU sets and emits no pin; `singleCorePinned` remains a one-CPU constraint.
- Effective configuration generation now writes concrete Mosquitto, EMQX, and ActiveMQ MQTT inputs with absolute, single-quoted bind sources. Direct host networking remains mandatory and no `ports:` stanza is generated.
- Capture honors `CaptureSeconds`, appending a one-second NDJSON sample per requested second. It labels cgroup memory accurately, records process RSS separately, uses cgroup I/O, and labels host-network connection/network figures as host-shared.
- The infra resolver now returns an `IDisposable` lease. It parses JSON nodes (including arrays), safely round-trips quotes/backslashes/newlines, uses a Windows owner-only ACL, and both benchmark callers dispose the runtime secret file with `using`/finally semantics.

### Fix-round RED/GREEN evidence

- RED: the lease-based resolver test did not compile before `Resolve` and `InfraConfigLease` existed; GREEN: the test verifies array expansion, JSON escaping, unset-variable safety, and disposal cleanup.
- RED: the pre-round generated override retained published ports; GREEN: controller tests assert host network mode, no ports, exact listener environment, lifecycle cleanup, and cgroup telemetry labels.

### Fix-round verification

- Focused controller/resolver tests: 9 passed, 0 failed.
- Full suite: `dotnet test src\EntityFX.MqttBenchmark.sln --no-restore` — 19 passed, 0 failed, 0 skipped.

## Fix round 4: remote execution and complete lifecycle verification

This round supersedes the earlier remote-verification limitations. Work began at `dfda1b1` on `codex/dissertation-readiness` and stayed within `MqttBenchmark`.

### Architecture and behavior

- The controller now selects one service from a JSON-formatted, YAML-compatible Compose document. There are no host bind mounts or published ports. Exact effective configuration bytes are encoded into environment values, decoded by the container entrypoint, and verified with in-container SHA-256. The same path works from Windows against the remote Linux daemon.
- Start saves `effective-compose.yml`, effective configuration files, and `run-provenance.json`. Capture checks the running container ID/image and deployed configuration hashes against that manifest; it does not regenerate configuration. The recorded inputs include the inventory, controller, effective Compose document, effective files, and custom build inputs/provenance.
- Aedes and ActiveMQ build the exact selected inventory tag. Per-service `build/*.json` records the context, image ID, and complete relevant build input hashes. Validation/start/health/capture reject unrelated records, changed inputs, retagged images, malformed identities, and a selected image that differs from the running image. Custom digest fields remain null because their local image IDs are not registry manifest digests. Registry image digest validation cannot be bypassed by an unrelated build record.
- Stop/reset retain inventory validation and permit cleanup after source changes or a failed rebuild. This is an explicit cleanup ruling: stopping the selected container must not require rebuilding it first.
- A single `docker exec` owns the telemetry loop. Each sample is scheduled from `/proc/uptime` against the original monotonic deadline, so Docker/SSH round trips do not accumulate per sample. CPU/throttle, memory, and I/O remain container cgroup counters; broker process RSS is separate; network/TCP tables are labeled host-shared. The sampler uses standard `sh`, `awk`, and `/proc`, without requiring `ss` in minimal images.
- ActiveMQ MQTT and Jetty HTTP both bind the selected control address. Its actual 6.3.2 `jetty-spring.xml` integration is enabled, and a hashed `stand-setenv` file sourced after distribution defaults disables the JVM management agent. Log4j and Java logging use warning/error console output with no rolling application files. EMQX dashboard, distribution, and RPC listeners bind the control address; optional MQTT transports are disabled. Mosquitto includes both warning and error logging; routine Aedes readiness/connection logging is silent. All services retain bounded Docker local logging, host networking, 4 GiB memory, and `unless-stopped` restart behavior.
- Windows secret files receive an owner-only protected ACL atomically at creation, before plaintext is written. Unix files are restricted to mode 0600 while still empty. All subsequent construction failures dispose handles and remove the owned path. Existing benchmark callers retain `using` disposal on normal and exceptional exits.

### Additional failures exposed by live execution

- The first live Mosquitto start and MQTT health passed; its initial capture failed because `awk` parsed a comparison in `printf` as output redirection. Parenthesizing the comparison fixed the actual sampler. Subsequent real captures produced five samples over 4.00 seconds.
- The remote build network required the arranged outbound proxy. The first npm run timed out, reported `Exit handler never called!`, and nevertheless exited zero, leaving empty package directories. A build-time `require('aedes')`/API/exact-version check now prevents such an image from being certified.
- Both custom builds support optional `MQTTY_BUILD_HTTP_PROXY`/`MQTTY_BUILD_NO_PROXY` environment references and use host build networking. The proxy endpoint is not stored in Compose, provenance, or runtime images. Ordinary hosts need no proxy setting. This host used Compose's classic builder fallback because its buildx plugin is absent; all requested image builds completed successfully.
- Once network access worked, real npm integrity verification exposed three incorrect pre-existing lockfile hashes: `debug@4.4.3`, `safe-buffer@5.2.1`, and `undici-types@8.9.0`. Every locked package was compared against npm registry metadata; exactly these three hashes differed. They were corrected without changing any version, and the complete real Node 20 image build then passed.
- The Aedes package exports its module but not `aedes/package.json`; version capture now reads the installed package metadata file directly. Live capture records Aedes 1.1.2 and Node v20.20.2.
- EMQX initially rejected the replaced configuration because required `node.cookie` and `node.data_dir` fields were missing. Its complete effective configuration now starts successfully, including the configured control-only internal listeners.

### Test-first evidence

- Replaced the previous broad-pattern Docker shim with command- and argument-specific responses, stateful container identity, actual configuration payload hashes, an unsupported-command failure, and a real loopback MQTT CONNECT/CONNACK listener.
- Initial controller RED: 11 failed, 1 passed. The failures exposed the unrelated-provenance digest bypass, incompatible remote Compose lifecycle, missing custom provenance behavior, and capture/start contracts.
- Secret lease RED: 1 failed, 2 passed. A real inherited Windows ACL induced the old post-allocation failure, leaving one runtime file. GREEN: 3/3 passed, including protected owner-only ACL inspection, escaped array secrets, missing-variable safety, and successful/exceptional caller cleanup.
- Selected-versus-running-image RED: 2/2 failed because capture accepted the old running image after the requested identity changed. GREEN: both now reject the mismatch.
- Optional proxy/version-path RED: 3 failed, 1 passed. GREEN followed the supported proxy argument and installed package metadata changes.
- ActiveMQ distribution management override RED: 2 failed, 2 passed. GREEN plus real process-command inspection confirms the JVM management agent is absent.
- EMQX internal listener binding RED: 1 failed, 3 passed. GREEN plus real socket-table inspection confirms the distribution/RPC bindings.
- Cleanup-after-source-edit RED: 2/2 failed at stop. GREEN permits both stop and reset without weakening deployment identity checks.
- Final full verification: `dotnet test src/EntityFX.MqttBenchmark.sln --no-restore --logger 'console;verbosity=minimal'` — **31 passed, 0 failed, 0 skipped**. This includes 21 focused controller/secret cases and 10 existing tests. The pre-existing nullable warning in `Benchmark.cs` remains unrelated; the final incremental run introduced no warning.
- `git diff --check` passed; only Git's existing LF/CRLF conversion notices were printed. `docker --context mqtty-stand compose -f docker/brokers/compose.yml --profile '*' config --quiet` passed for all four services.

### Real remote lifecycle evidence

Docker CLI 29.8.0 on Windows connected through context `mqtty-stand` to Linux Docker Engine 29.2.1. Each run completed pull/build, validate, start, actual MQTT health, five-sample capture, stop, and reset. Pinned runs used `singleHostSequential`; all-core runs used `distributedHosts` and MQTT port 1883. Actual container inspection verified direct host networking, memory `4294967296`, and CPU set `0` or empty as appropriate. Services were run sequentially.

| Broker | CPU mode | Samples | Monotonic span | First broker RSS, bytes |
| --- | --- | ---: | ---: | ---: |
| Aedes | singleCorePinned | 5 | 4.00 s | 63,119,360 |
| Aedes | hostAllCores | 5 | 4.00 s | 62,083,072 |
| Mosquitto | singleCorePinned | 5 | 4.00 s | 6,348,800 |
| Mosquitto | hostAllCores | 5 | 4.00 s | 6,414,336 |
| ActiveMQ | singleCorePinned | 5 | 4.00 s | 163,258,368 |
| ActiveMQ | hostAllCores | 5 | 4.00 s | 200,335,360 |
| EMQX | singleCorePinned | 5 | 4.01 s | 400,936,960 |
| EMQX | hostAllCores | 5 | 4.00 s | 433,987,584 |

Final running image identities:

- Aedes 1.1.2 / Node v20.20.2: `sha256:f53f3500644d4af1adfec41dd0ea0f76b2fca840673e26fe329475a3d9388170`.
- Mosquitto 2.1.2: `sha256:4725ea7590a578122675b1fdd242b94e5034885cfd0806045b25d700f5498ef5`.
- ActiveMQ 6.3.2 / Temurin Java 21.0.12: `sha256:6e80e8c116d6769b3d7eeecf446fd0d3c779302d1c452df722ef3ad9bb11c087`.
- EMQX 6.3.0: `sha256:8de467e93f025f4c5895a5304df39bc7e74d62b77731ab14e6b677f19b2c0ffb`.

Live assertions checked ActiveMQ MQTT 3883/1883 and HTTP 8161 on `10.10.157.111` only, absence of the JVM management-agent argument, and EMQX dashboard 18083, distribution 4370, and RPC 5369 on that control address only. The pre-existing bare-metal MQTT listener on 9883 remained reachable and was not changed. The stopped temporary container from the failed npm build was identified by its exact ID/command and removed; its contents are reproducible public build inputs.

The local evidence harness is `.superpowers/sdd/four-broker-stand-implementation-plan/round-4-live.ps1`. Per-run evidence under its `round-4-live/` directory includes inventories, effective configs, build/start provenance, actual container inspection, versions, telemetry, logs, socket tables, and hashes. These runtime artifacts are intentionally ignored by Git; this report is tracked. For example:

```powershell
& .superpowers/sdd/four-broker-stand-implementation-plan/round-4-live.ps1 -Brokers @('EMQX') -CpuMode hostAllCores -DeploymentMode distributedHosts
```

### Boundaries and sources

No Task 1 residual is left unverified in the available stand. The telemetry implementation explicitly targets Linux cgroup v2; host network/socket counters remain host-shared. Distribution startup banners can appear once even though steady-state application logging is warning/error. A new output directory is recommended for each campaign run, as documented in `docker/brokers/README.md`.

Primary references used to verify version-specific behavior: [ActiveMQ 6.3.2 Jetty integration](https://github.com/apache/activemq/blob/activemq-6.3.2/assembly/src/release/conf/jetty-spring.xml), [ActiveMQ 6.3.2 HTTP connector](https://github.com/apache/activemq/blob/activemq-6.3.2/assembly/src/release/conf/jetty/jetty-http.xml), [EMQX configuration schema](https://github.com/emqx/emqx/blob/master/apps/emqx_conf/src/emqx_conf_schema.erl), [EMQX 6 changes](https://docs.emqx.com/en/emqx/latest/changes/changes-ee-v6.html), and npm metadata for [debug 4.4.3](https://registry.npmjs.org/debug/4.4.3), [safe-buffer 5.2.1](https://registry.npmjs.org/safe-buffer/5.2.1), and [undici-types 8.9.0](https://registry.npmjs.org/undici-types/8.9.0). Configuration/API findings were additionally verified against the pinned images and running containers.
