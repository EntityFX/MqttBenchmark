using System.Text.Json;

namespace EntityFX.MqttBenchmark.Calibration;

public static class BenchmarkMatrixStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static BenchmarkMatrixDefinition Read(string path)
    {
        BenchmarkMatrixDefinition matrix;
        try
        {
            matrix = JsonSerializer.Deserialize<BenchmarkMatrixDefinition>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Benchmark matrix JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Benchmark matrix JSON is invalid.", exception);
        }
        matrix.Validate();
        return matrix;
    }
}
