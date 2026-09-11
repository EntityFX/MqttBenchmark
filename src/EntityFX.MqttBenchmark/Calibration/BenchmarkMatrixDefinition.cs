namespace EntityFX.MqttBenchmark.Calibration;

public sealed class BenchmarkMatrixDefinition
{
    public int SchemaVersion { get; set; } = 2;
    public IReadOnlyList<BenchmarkBrokerEndpoint> Brokers { get; set; } = Array.Empty<BenchmarkBrokerEndpoint>();
    public IReadOnlyList<int> MessageBytes { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Qos { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Clients { get; set; } = Array.Empty<int>();
    public int Repeats { get; set; }
    public double WarmupSeconds { get; set; }
    public double MeasurementSeconds { get; set; }

    public void Validate()
    {
        if (SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported matrix schemaVersion={SchemaVersion}; expected 2.");
        if (Brokers == null || Brokers.Count == 0)
            throw new InvalidDataException("At least one broker endpoint is required.");
        if (Brokers.Any(item => item == null || string.IsNullOrWhiteSpace(item.Name) ||
                item.Uri == null || !item.Uri.IsAbsoluteUri || item.Uri.Port <= 0) ||
            Brokers.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Brokers.Count)
            throw new InvalidDataException("Broker names must be unique and endpoints must be absolute URIs with a port.");
        ValidateDimension(MessageBytes, nameof(MessageBytes), value => value > 0);
        ValidateDimension(Qos, nameof(Qos), value => value is >= 0 and <= 2);
        ValidateDimension(Clients, nameof(Clients), value => value > 0);
        if (Repeats <= 0) throw new InvalidDataException("Repeats must be positive.");
        if (!double.IsFinite(WarmupSeconds) || WarmupSeconds < 0)
            throw new InvalidDataException("WarmupSeconds must be finite and non-negative.");
        if (!double.IsFinite(MeasurementSeconds) || MeasurementSeconds <= 0)
            throw new InvalidDataException("MeasurementSeconds must be finite and positive.");
    }

    public IEnumerable<BenchmarkRunSpec> Expand()
    {
        Validate();
        foreach (var broker in Brokers)
        foreach (var messageBytes in MessageBytes)
        foreach (var qos in Qos)
        foreach (var clients in Clients)
        for (var repeat = 1; repeat <= Repeats; repeat++)
            yield return new BenchmarkRunSpec(
                broker, messageBytes, qos, clients, repeat, WarmupSeconds, MeasurementSeconds);
    }

    private static void ValidateDimension(
        IReadOnlyList<int>? values, string name, Func<int, bool> predicate)
    {
        if (values == null || values.Count == 0 || values.Any(value => !predicate(value)) ||
            values.Distinct().Count() != values.Count)
            throw new InvalidDataException($"{name} must contain unique valid values.");
    }
}
