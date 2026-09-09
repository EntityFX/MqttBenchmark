# MqttBenchmark

The reproducible calibration pipeline is driven by
`config/benchmark-matrix.v2.json`. The versioned default expands to four brokers,
two exact payload sizes, three QoS levels, eight client counts and ten repeats
(1920 immutable runs).

Preview the matrix without contacting brokers:

```powershell
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
  --config config/benchmark-matrix.v2.json `
  --output results/raw `
  --dry-run
```

Run a campaign. Each completed point is written immediately with `FileMode.CreateNew`
under a unique campaign directory, so an existing raw result is never overwritten:

```powershell
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
  --config config/benchmark-matrix.v2.json `
  --output results/raw `
  --campaign dissertation-20260909
```

For an infrastructure smoke test, add `--broker Mosquitto --max-runs 1`.
The full sequential matrix needs at least 18.7 hours for its configured warm-up and
measurement windows, before connection and delivery-drain overhead.

Aggregate one complete campaign and generate the runtime profile:

```powershell
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- aggregate `
  --config config/benchmark-matrix.v2.json `
  --raw results/raw/dissertation-20260909 `
  --output results/aggregated/dissertation-20260909 `
  --mqtt-y-repo C:/projects/asp/mqtty `
  --benchmark-repo .
```

The aggregate command requires exact matrix coverage and repeats 1 through 10 for
every point. It writes `aggregated.csv`, `aggregated.json`, and
`broker-benchmark.v2.json` using create-new semantics. The profile records both Git
SHAs and SHA-256 for every input CSV.

Metric definitions are fixed:

- `CompletedRps = ok / measurementSeconds`
- `PublishFailureRate = failed / requestCount`
- `DeliveryLossRate = 1 - received / ok` for completed publishes; it is zero when
  no publish completed because conditional delivery loss is then unobservable.

QoS 1/2 latency knots are nearest-rank values from `PublishAsync` completion times.
QoS 0 latency is always `null` and is excluded from fidelity calibration.
