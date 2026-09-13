using System.Security.Cryptography;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public static class CampaignJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    public static void WriteNew<T>(string path, T value) => WriteBytesNew(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));
    public static void WriteBytesNew(string path, byte[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(value);
        stream.Flush(true);
    }
    public static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));
    /// <summary>
    /// Хэш-дерево артефактов каталога. Исключения: <c>.lock</c> (lease-файлы) и каталог
    /// <c>logs/</c> — живые stdout/stderr нативного брокера, открытые его процессом и
    /// недопустимые для атомарного снапшота во время измерения.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HashTree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != ".lock")
        .Where(p => !Path.GetRelativePath(root, p).Replace('\\', '/').StartsWith("logs/", StringComparison.Ordinal))
        .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), HashFile, StringComparer.Ordinal);
}
