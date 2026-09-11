namespace EntityFX.MqttBenchmark.Calibration;

public sealed record BenchmarkRunSpec(
    BenchmarkBrokerEndpoint Broker,
    int MessageBytes,
    int Qos,
    int Clients,
    int Repeat,
    double WarmupSeconds,
    double MeasurementSeconds);
