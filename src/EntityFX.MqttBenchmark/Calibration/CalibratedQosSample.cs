namespace EntityFX.MqttBenchmark.Calibration;

public sealed record CalibratedQosSample(
    int Clients,
    double CapacityRps,
    double PublishFailureRate,
    double ConditionalDeliveryLossRate,
    LatencyQuantiles? ProcessingLatencyQuantiles);
