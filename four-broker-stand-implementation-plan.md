# Four-broker stand implementation plan

## Global constraints

- Work in the existing `codex/dissertation-readiness` branches of `MqttBenchmark` and `mqtty`; preserve unrelated changes and commit each repository independently.
- Use TDD for production behavior: add a failing behavioral test, observe RED, implement the minimum change, then observe GREEN. Configuration and human documentation do not require change-detector tests.
- Never store credentials in source, manifests, logs, reports, or command arguments. Existing plaintext credentials in `infra-config.json` must be replaced by environment/secret references; external credential rotation is reported as an operator action.
- Raw benchmark and fidelity artifacts remain local and uncommitted. Commit configs, manifests, SHA-256 registry, aggregates, observations/calibration, compact summaries/logs, and representative relay bundles only.
- Do not invent successful real runs. A missing broker, Docker capability, management endpoint, or comparable resource manifest is a recorded blocker.
- ActiveMQ means ActiveMQ Classic, never Artemis. Floating image tags are forbidden; every broker requires an immutable digest.
- Results are aggregatable only within one deployment mode and one CPU mode. `singleCorePinned` is the mandatory baseline; `hostAllCores` is a separate optional campaign.

## Task 1: Reproducible broker stand and controller

In `MqttBenchmark`, add `docker/brokers/compose.yml`, an Aedes 1.1.2 image pinned to a Node runtime with `package-lock.json`, and explicit configs for Mosquitto 2.1.2, EMQX 6.3.0, and ActiveMQ Classic 6.3.2. Single-host ports are Aedes 1883, Mosquitto 2883, ActiveMQ 3883, EMQX 4883; distributed hosts use 1883. Disable persistence and align logging/restart/memory behavior. Management ports bind only to the control interface.

Add `config/broker-stand.v1.json` and a PowerShell controller supporting `validate`, `pull`, `start`, `health`, `reset`, `capture`, and `stop`. Inventory fields include deployment/cpu modes, active broker, docker context, MQTT/management URIs, image, digest, cpuset, and memory limit. Validation rejects missing digests, duplicate endpoints, multiple loaded brokers in single-host mode, and incompatible campaign topology/CPU modes. Capture one-second telemetry for CPU/throttling, RSS, network, disk I/O, connections, container logs, versions, and config hashes. Replace plaintext credentials in `src/EntityFX.MqttBenchmark.Bomber/infra-config.json` with environment/secret references without exposing previous values.

Add behavioral tests for schema/inventory validation, exclusive broker load, CPU modes, pinned digests, controller validation, version/config capture, and cleanup semantics. Commit the task and report RED/GREEN evidence.

## Task 2: MqttBenchmark campaign schema v3 and immutable runner

In `MqttBenchmark`, add `config/benchmark-campaign.v3.json` for 4 brokers, payload 16/256 bytes, QoS 0/1/2, publishers 1/16/64/128, three repeats, 5 s warm-up, 30 s measurement, 5 s cooldown, 30 s drain timeout, 2 s quiet period, and seed 20260909. Extend CLI with `preflight --stand ... --config ... --output ...`, `matrix --stand ... --config ... --campaign ... --resume`, and `aggregate --config ... --raw ... --output ...`.

Each exact-size payload contains a deterministic 16-byte ID. Record attempts, completed publishes, error reasons, unique/duplicate/unexpected deliveries, and delivery after failed publish. Use actual measurement duration for attempted/completed RPS and failure/loss formulas. QoS 0 publish latency is not applicable; QoS 1/2 use end-to-end completion latency. Drain finishes after all completed IDs are delivered or after quiet-period/timeout. Attempts are immutable; resume skips successful keys, permits two further attempts with separate artifacts, and stops the campaign after the third failed attempt for a key.

Preflight performs CONNECT/SUBSCRIBE/PUBLISH for QoS 0/1/2, 20 RTT probes, `$SYS/broker/version`, environment capture, and telemetry manifest. Aggregate only at full 288/288 coverage and calculate Student-t CI95. Emit `broker-observations.v3.json` with provenance. Add behavioral unit/integration tests for deterministic ordering, exact payload IDs, drain, immutable retry/resume, statistics, aggregation compatibility, and preflight output. Commit the task and report RED/GREEN evidence.

## Task 3: MqttY calibration v3 and topology fidelity

In `mqtty`, add a `calibrate` command consuming `broker-observations.v3.json` and writing `broker-calibration.v3.json`. Schema v3 stores AttemptedRps, target CompletedRps, conditional publish-failure and delivery-loss rates, observed/processing latency, RTT baseline, and provenance. Calculate `Fconditional = (Ftarget - Frate) / (1 - Frate)` and reject points where `Frate > Ftarget`. Subtract one RTT from QoS 1 latency knots and two RTTs from QoS 2, clamped at zero; QoS 0 remains not applicable.

Fix external profile loading so the exact bytes hashed by the command are parsed by the runner. Add `ExperimentTopologyMode = BrokerFidelity | MqttRelay` and `ProfileClientCountMode = Publishers | ConnectedClients`. BrokerFidelity creates N publishers plus one subscriber. MqttRelay keeps relay topology but selects profile clients according to the configured semantic. Do not replace the embedded four-broker profile.

Add behavioral tests for calibration formulas/provenance, external profile byte identity, both topology/client-count modes, and deterministic replay. Commit the task and report RED/GREEN evidence.

## Task 4: Real execution, protocol, and dissertation documentation

From the Windows controller, validate/deploy/health-check all available brokers. Existing Aedes `localhost:1883` and Mosquitto `10.10.157.111:9883` are pilot endpoints only unless comparable resource manifests are proven. Run preflight and a short pilot for each available broker/QoS; then run the 288-point single-core baseline only if all four brokers have equivalent pinned resources and green preflight/pilot. Run 96 BrokerFidelity points for three seeds and representative MqttRelay smoke coverage. Because the stated count 12 conflicts with `4 brokers x 2 payloads x 3 QoS = 24`, full Cartesian coverage is 24 and is the binding interpretation. The optional all-core campaign remains separate.

Create `diss/doc/dissertation-readiness/task-8-real-broker-validation-report.md`; update `plan.md`, `progress.md`, and architecture chapters 06/10/12. Include environment/manifests, command transcript, hashes, coverage, fidelity thresholds (CompletedRps <=15%, p99 QoS1/2 <=20%, failure/loss absolute <=0.05, QoS0 latency not applicable), limited statistical power from three repeats, honest blockers, and recommended MqttY/MqttBenchmark improvements. Commit documentation in the repository that owns it, and report verification evidence.
