namespace EntityFX.MqttBenchmark.Calibration;

public sealed record ProfileProvenance(
    string MqttYCommitSha,
    string MqttBenchmarkCommitSha,
    IReadOnlyDictionary<string, string> InputCsvSha256,
    DateTimeOffset GeneratedAtUtc,
    double WarmupSeconds,
    double MeasurementSeconds,
    int Repeats);
