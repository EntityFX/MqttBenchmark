using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Bomber;

public static class InfraConfigEnvironmentResolver
{
    private static readonly Regex EnvironmentReference = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static string ResolveToTemporaryFile(string sourcePath)
    {
        var source = JsonNode.Parse(File.ReadAllText(sourcePath)) ?? throw new InvalidDataException("Infrastructure configuration is empty.");
        ResolveNode(source);
        var destination = Path.GetTempFileName();
        File.WriteAllText(destination, source.ToJsonString());
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.Temporary);
        return destination;
    }

    private static void ResolveNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
                if (property.Value is not null) ResolveNode(property.Value);
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array)
                if (item is not null) ResolveNode(item);
            return;
        }
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
        var match = EnvironmentReference.Match(text);
        if (!match.Success || match.Value != text) return;
        var name = match.Groups[1].Value;
        var resolved = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(resolved))
            throw new InvalidDataException($"Required environment variable '{name}' is not set.");
        value.ReplaceWith(resolved);
    }
}
