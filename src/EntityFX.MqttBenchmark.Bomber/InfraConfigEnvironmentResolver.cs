using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EntityFX.MqttBenchmark.Bomber;

public static class InfraConfigEnvironmentResolver
{
    private static readonly Regex EnvironmentReference = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static InfraConfigLease Resolve(string sourcePath)
    {
        var source = JsonNode.Parse(File.ReadAllText(sourcePath)) ?? throw new InvalidDataException("Infrastructure configuration is empty.");
        ResolveNode(source);
        var destination = Path.GetTempFileName();
        File.WriteAllText(destination, source.ToJsonString());
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.Temporary | FileAttributes.Hidden);
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(destination).SetAccessControl(security);
        }
        return new InfraConfigLease(destination);
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
            for (var index = 0; index < array.Count; index++)
                if (array[index] is not null) ResolveNode(array[index]!);
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

public sealed class InfraConfigLease : IDisposable
{
    public InfraConfigLease(string path) => Path = path;
    public string Path { get; }

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
