namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Результат preflight одного брокера: протокольная проба и телеметрия без нагрузки.</summary>
public sealed record BrokerPreflight(ProtocolPreflight Protocol, TelemetryManifest Telemetry);
