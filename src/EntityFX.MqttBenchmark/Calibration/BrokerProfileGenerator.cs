using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityFX.MqttBenchmark.Calibration;

public static class BrokerProfileGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static BrokerProfileDocument Generate(
        IEnumerable<AggregatedBenchmarkPoint> points, ProfileProvenance provenance)
    {
        var values = points.OrderBy(point => point.Broker, StringComparer.Ordinal)
            .ThenBy(point => point.MessageBytes).ThenBy(point => point.Qos)
            .ThenBy(point => point.Clients).ToArray();
        Validate(values, provenance);

        var brokers = values.GroupBy(point => point.Broker)
            .Select(broker => new BrokerProfileEntry(broker.Key,
                broker.GroupBy(point => point.MessageBytes)
                    .Select(message => new MessageSizeProfileEntry(message.Key,
                        message.GroupBy(point => point.Qos)
                            .Select(qos => new QosProfileEntry(qos.Key,
                                qos.Select(ToSample).ToArray()))
                            .OrderBy(qos => qos.Qos).ToArray()))
                    .OrderBy(message => message.MessageBytes).ToArray()))
            .ToArray();

        return new BrokerProfileDocument(2, "measured", provenance.GeneratedAtUtc,
            provenance.MqttYCommitSha, provenance.MqttBenchmarkCommitSha,
            new SortedDictionary<string, string>(
                provenance.InputCsvSha256.ToDictionary(item => item.Key, item => item.Value),
                StringComparer.Ordinal),
            values.Sum(point => point.Repeats), provenance.WarmupSeconds,
            provenance.MeasurementSeconds, provenance.Repeats, brokers);
    }

    public static string Serialize(BrokerProfileDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static void WriteNew(string path, BrokerProfileDocument document)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, document, JsonOptions);
    }

    private static CalibratedQosSample ToSample(AggregatedBenchmarkPoint point)
    {
        LatencyQuantiles? latency = point.ProcessingLatencyQuantiles == null ? null : new LatencyQuantiles(
            point.ProcessingLatencyQuantiles.MinMs.Mean,
            point.ProcessingLatencyQuantiles.P50Ms.Mean,
            point.ProcessingLatencyQuantiles.P75Ms.Mean,
            point.ProcessingLatencyQuantiles.P95Ms.Mean,
            point.ProcessingLatencyQuantiles.P99Ms.Mean,
            point.ProcessingLatencyQuantiles.MaxMs.Mean);
        latency?.Validate();
        return new CalibratedQosSample(point.Clients, point.CompletedRps.Mean,
            point.PublishFailureRate.Mean, point.DeliveryLossRate.Mean, latency);
    }

    private static void Validate(AggregatedBenchmarkPoint[] points, ProfileProvenance provenance)
    {
        if (points.Length == 0) throw new InvalidDataException("No aggregate points were supplied.");
        if (string.IsNullOrWhiteSpace(provenance.MqttYCommitSha) ||
            string.IsNullOrWhiteSpace(provenance.MqttBenchmarkCommitSha))
            throw new InvalidDataException("Both repository commit SHAs are required.");
        if (provenance.Repeats <= 0 || provenance.MeasurementSeconds <= 0 || provenance.WarmupSeconds < 0)
            throw new InvalidDataException("Profile measurement boundaries are invalid.");
        if (points.Any(point => point.Repeats != provenance.Repeats))
            throw new InvalidDataException("Aggregate repeat count differs from provenance.");
        if (points.Any(point => point.CompletedRps.Mean <= 0 ||
                point.PublishFailureRate.Mean is < 0 or > 1 ||
                point.DeliveryLossRate.Mean is < 0 or > 1))
            throw new InvalidDataException("Aggregate capacity or failure rates are invalid.");
        foreach (var hash in provenance.InputCsvSha256.Values)
        {
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("Every input CSV SHA-256 must contain 64 hexadecimal digits.");
        }
        foreach (var group in points.GroupBy(point => (point.Broker, point.MessageBytes)))
        {
            if (!group.Select(point => point.Qos).Distinct().OrderBy(value => value)
                    .SequenceEqual(new[] { 0, 1, 2 }))
                throw new InvalidDataException(
                    $"{group.Key.Broker}/m{group.Key.MessageBytes} must contain QoS 0, 1 and 2.");
            foreach (var qos in group.GroupBy(point => point.Qos))
            {
                var clients = qos.Select(point => point.Clients).ToArray();
                if (clients.Any(value => value <= 0) || clients.Distinct().Count() != clients.Length ||
                    !clients.SequenceEqual(clients.OrderBy(value => value)))
                    throw new InvalidDataException("Client calibration points must be positive, unique and sorted.");
                if (qos.Key == 0 && qos.Any(point => point.ProcessingLatencyQuantiles != null) ||
                    qos.Key > 0 && qos.Any(point => point.ProcessingLatencyQuantiles == null))
                    throw new InvalidDataException("QoS latency applicability is invalid.");
            }
        }
    }
}
