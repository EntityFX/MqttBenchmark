namespace EntityFX.MqttBenchmark.Calibration;

public sealed record QosProfileEntry(int Qos, IReadOnlyList<CalibratedQosSample> Samples);
