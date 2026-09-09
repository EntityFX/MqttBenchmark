namespace EntityFX.MqttBenchmark.Calibration;

public sealed record RawBenchmarkResult(
    string RunId,
    string Broker,
    Uri BrokerUri,
    int MessageBytes,
    int Qos,
    int Clients,
    int Repeat,
    double WarmupSeconds,
    double MeasurementSeconds,
    long RequestCount,
    long Ok,
    long Failed,
    long Received,
    LatencyQuantiles? PublishLatencyMs,
    DateTimeOffset MeasurementStartedUtc,
    DateTimeOffset MeasurementEndedUtc)
{
    public double CompletedRps => Ok / MeasurementSeconds;
    public double PublishFailureRate => RequestCount == 0 ? 0 : Failed / (double)RequestCount;
    public double DeliveryLossRate => Ok == 0 ? 0 : 1 - Received / (double)Ok;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId) || string.IsNullOrWhiteSpace(Broker))
            throw new InvalidDataException("RunId and Broker are required.");
        if (BrokerUri == null || !BrokerUri.IsAbsoluteUri || BrokerUri.Port <= 0)
            throw new InvalidDataException("BrokerUri must be absolute and include a port.");
        if (MessageBytes <= 0 || Qos is < 0 or > 2 || Clients <= 0 || Repeat <= 0)
            throw new InvalidDataException("MessageBytes, QoS, Clients or Repeat is invalid.");
        if (!double.IsFinite(WarmupSeconds) || WarmupSeconds < 0 ||
            !double.IsFinite(MeasurementSeconds) || MeasurementSeconds <= 0)
            throw new InvalidDataException("Measurement boundaries are invalid.");
        if (RequestCount < 0 || Ok < 0 || Failed < 0 || Received < 0 ||
            RequestCount != Ok + Failed || Received > Ok)
            throw new InvalidDataException("Request/outcome counts are inconsistent.");
        if (MeasurementEndedUtc < MeasurementStartedUtc)
            throw new InvalidDataException("Measurement end precedes its start.");
        if (Qos == 0 && PublishLatencyMs != null)
            throw new InvalidDataException("QoS 0 publish latency must be notApplicable/null.");
        if (Qos > 0 && PublishLatencyMs == null && Ok > 0)
            throw new InvalidDataException("QoS 1/2 successful publishes require latency quantiles.");
        PublishLatencyMs?.Validate();
    }
}
