using System.Text.RegularExpressions;

namespace EntityFX.MqttBenchmark.Bomber;

public static class InfraConfigEnvironmentResolver
{
    private static readonly Regex EnvironmentReference = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static string ResolveToTemporaryFile(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var resolved = EnvironmentReference.Replace(source, match =>
        {
            var name = match.Groups[1].Value;
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                throw new InvalidDataException($"Required environment variable '{name}' is not set.");
            return value;
        });

        var destination = Path.GetTempFileName();
        File.WriteAllText(destination, resolved);
        return destination;
    }
}
