using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EntityFX.MqttBenchmark.Calibration;

public static class RawArtifactStore
{
    private static readonly string[] Header =
    {
        "runId", "broker", "brokerUri", "messageBytes", "qos", "clients", "repeat",
        "warmupSeconds", "measurementSeconds", "requestCount", "ok", "failed", "received",
        "completedRps", "publishFailureRate", "deliveryLossRate",
        "latencyMinMs", "latencyP50Ms", "latencyP75Ms", "latencyP95Ms", "latencyP99Ms", "latencyMaxMs",
        "measurementStartedUtc", "measurementEndedUtc"
    };

    public static string WriteUnique(string directory, RawBenchmarkResult result)
    {
        Directory.CreateDirectory(directory);
        var broker = string.Concat(result.Broker.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'));
        var name = $"{broker}.m{result.MessageBytes}.q{result.Qos}.c{result.Clients}.r{result.Repeat}.{result.RunId}.csv";
        var path = Path.Combine(directory, name);
        WriteNew(path, result);
        return path;
    }

    public static void WriteNew(string path, RawBenchmarkResult result)
    {
        result.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(string.Join(',', Header));
        writer.WriteLine(string.Join(',', Values(result).Select(Escape)));
    }

    public static RawBenchmarkResult Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length != 2 || !ParseLine(lines[0]).SequenceEqual(Header))
            throw new InvalidDataException($"Raw benchmark CSV '{path}' has an unsupported shape.");
        var values = ParseLine(lines[1]);
        if (values.Count != Header.Length)
            throw new InvalidDataException($"Raw benchmark CSV '{path}' has {values.Count} values; expected {Header.Length}.");
        var latency = string.IsNullOrEmpty(values[16]) ? null : new LatencyQuantiles(
            Double(values[16]), Double(values[17]), Double(values[18]),
            Double(values[19]), Double(values[20]), Double(values[21]));
        var result = new RawBenchmarkResult(
            values[0], values[1], new Uri(values[2]), Int(values[3]), Int(values[4]),
            Int(values[5]), Int(values[6]), Double(values[7]), Double(values[8]),
            Long(values[9]), Long(values[10]), Long(values[11]), Long(values[12]), latency,
            DateTimeOffset.Parse(values[22], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DateTimeOffset.Parse(values[23], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        result.Validate();
        AssertComputed("completedRps", Double(values[13]), result.CompletedRps);
        AssertComputed("publishFailureRate", Double(values[14]), result.PublishFailureRate);
        AssertComputed("deliveryLossRate", Double(values[15]), result.DeliveryLossRate);
        return result;
    }

    public static IReadOnlyList<RawBenchmarkResult> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Read).ToArray();

    public static IReadOnlyDictionary<string, string> HashTree(string root)
    {
        var rootPath = Path.GetFullPath(root);
        return Directory.EnumerateFiles(rootPath, "*.csv", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> Values(RawBenchmarkResult result)
    {
        string D(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        string L(long value) => value.ToString(CultureInfo.InvariantCulture);
        yield return result.RunId;
        yield return result.Broker;
        yield return result.BrokerUri.ToString();
        yield return L(result.MessageBytes);
        yield return L(result.Qos);
        yield return L(result.Clients);
        yield return L(result.Repeat);
        yield return D(result.WarmupSeconds);
        yield return D(result.MeasurementSeconds);
        yield return L(result.RequestCount);
        yield return L(result.Ok);
        yield return L(result.Failed);
        yield return L(result.Received);
        yield return D(result.CompletedRps);
        yield return D(result.PublishFailureRate);
        yield return D(result.DeliveryLossRate);
        foreach (var value in result.PublishLatencyMs == null
            ? Enumerable.Repeat(string.Empty, 6)
            : new[] { D(result.PublishLatencyMs.MinMs), D(result.PublishLatencyMs.P50Ms),
                D(result.PublishLatencyMs.P75Ms), D(result.PublishLatencyMs.P95Ms),
                D(result.PublishLatencyMs.P99Ms), D(result.PublishLatencyMs.MaxMs) })
            yield return value;
        yield return result.MeasurementStartedUtc.ToString("O", CultureInfo.InvariantCulture);
        yield return result.MeasurementEndedUtc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";

    private static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted CSV value.");
        values.Add(value.ToString());
        return values;
    }

    private static int Int(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static long Long(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double Double(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void AssertComputed(string name, double stored, double computed)
    {
        if (Math.Abs(stored - computed) > 1e-12)
            throw new InvalidDataException($"Stored {name} does not match raw counts.");
    }
}
