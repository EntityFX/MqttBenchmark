namespace EntityFX.MqttBenchmark.Calibration;

public sealed record ProfileProvenance(
    string MqttYCommitSha,
    string MqttBenchmarkCommitSha,
    IReadOnlyDictionary<string, string> InputCsvSha256,
    DateTimeOffset GeneratedAtUtc,
    double WarmupSeconds,
    double MeasurementSeconds,
    int Repeats);

public sealed record BrokerProfileDocument(
    int SchemaVersion,
    string CalibrationStatus,
    DateTimeOffset GeneratedAtUtc,
    string MqttYCommitSha,
    string MqttBenchmarkCommitSha,
    IReadOnlyDictionary<string, string> InputCsvSha256,
    int RunCount,
    double WarmupSeconds,
    double MeasurementSeconds,
    int Repeats,
    IReadOnlyList<BrokerProfileEntry> Brokers);

public sealed record BrokerProfileEntry(string Name, IReadOnlyList<MessageSizeProfileEntry> MessageSizes);
public sealed record MessageSizeProfileEntry(int MessageBytes, IReadOnlyList<QosProfileEntry> Qos);
public sealed record QosProfileEntry(int Qos, IReadOnlyList<CalibratedQosSample> Samples);
public sealed record CalibratedQosSample(
    int Clients,
    double CapacityRps,
    double PublishFailureRate,
    double ConditionalDeliveryLossRate,
    LatencyQuantiles? ProcessingLatencyQuantiles);
