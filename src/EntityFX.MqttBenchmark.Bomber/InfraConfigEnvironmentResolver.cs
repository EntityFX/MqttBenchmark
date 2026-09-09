using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace EntityFX.MqttBenchmark.Bomber;

public static class InfraConfigEnvironmentResolver
{
    private static readonly Regex EnvironmentReference = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static InfraConfigLease Resolve(string sourcePath)
    {
        var source = JsonNode.Parse(File.ReadAllText(sourcePath)) ?? throw new InvalidDataException("Infrastructure configuration is empty.");
        ResolveNode(source);
        var destination = Path.Combine(Path.GetTempPath(), "mqttbenchmark-infra-" + Guid.NewGuid().ToString("N") + ".json");
        var created = false;
        try
        {
            using (var stream = CreatePrivateFile(destination))
            {
                created = true;
                // Unix permissions are restricted while empty; Windows applies the ACL atomically at creation.
                if (!OperatingSystem.IsWindows() && Chmod(destination, 0x180 /* 0600 */) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restrict runtime infrastructure file permissions.");
                }
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.Temporary | FileAttributes.Hidden);
                using var writer = new StreamWriter(stream);
                writer.Write(source.ToJsonString());
            }
            return new InfraConfigLease(destination);
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }

    private static FileStream CreatePrivateFile(string destination)
    {
        if (!OperatingSystem.IsWindows()) return new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(destination).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.None, security);
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int Chmod(string pathname, int mode);

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
