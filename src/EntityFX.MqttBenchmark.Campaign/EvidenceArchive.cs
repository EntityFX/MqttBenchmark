using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ArchivedAttempt(string ArchivePath, string Sha256);

/// <summary>Cold storage of sealed attempts. Archive validation precedes any removal.
/// Transfers run after workload/guard writers have ended, outside the measured window.</summary>
public static class EvidenceArchive
{
    public const string ReceiptName = "archive.json";
    private static readonly string[] Compact = { "started.json", "result.json", "sha256.json" };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    public static void EnsureSpace(string archiveRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceEx(archiveRoot, out var free, out _, out _) || free < 20UL * 1024 * 1024 * 1024)
                throw new IOException("Evidence disk is unavailable or has less than 20 GiB free.");
        }
        else if (new DriveInfo(archiveRoot).AvailableFreeSpace < 20L * 1024 * 1024 * 1024)
            throw new IOException("Evidence disk has less than 20 GiB free.");
    }

    public static void EnsureLinuxMount(string archiveRoot, string mount, string device)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Explicit mount verification requires Linux.");
        var info = new ProcessStartInfo("findmnt") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-n", "-o", "TARGET,SOURCE,FSTYPE", "-T", archiveRoot }) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new IOException("Could not check evidence mount.");
        var output = process.StandardOutput.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        if (process.ExitCode != 0 || !output.SequenceEqual(new[] { mount, device, "ext4" }))
            throw new IOException("Evidence mount identity changed; archive and cleanup forbidden.");
    }

    public static void Archive(string attemptDirectory, string archiveRoot, Action? verifyStorage = null)
    {
        verifyStorage?.Invoke();
        var source = Path.GetFullPath(attemptDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Evidence archive refuses links.");
        if (File.Exists(Path.Combine(source, ReceiptName))) { Validate(source); verifyStorage?.Invoke(); Cleanup(source); return; }
        if (!File.Exists(Path.Combine(source, "sha256.json"))) throw new IOException("Only sealed attempts can be archived.");
        var key = Path.GetFileName(Path.GetDirectoryName(source));
        var destinationRoot = Path.GetFullPath(archiveRoot);
        if (destinationRoot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || destinationRoot == source)
            throw new IOException("Archive storage must be outside the attempt directory.");
        var destination = Path.Combine(destinationRoot, key!, Path.GetFileName(source) + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Recover a crash between publishing the validated ZIP and writing its receipt.
        if (File.Exists(destination))
        {
            var recovered = new ArchivedAttempt(Path.GetFullPath(destination), CampaignJson.HashFile(destination));
            ValidateArchive(source, destination, recovered.Sha256);
            CampaignJson.WriteNew(Path.Combine(source, ReceiptName), recovered);
            verifyStorage?.Invoke(); Cleanup(source); return;
        }
        // A previous interrupted transfer is retained under a distinct name; never overwrite it.
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (relative == ".lock" || relative == ReceiptName) continue;
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            output.Flush(true);
        }
        var receipt = new ArchivedAttempt(Path.GetFullPath(destination), CampaignJson.HashFile(staging));
        ValidateArchive(source, staging, receipt.Sha256);
        File.Move(staging, destination, overwrite: false);
        CampaignJson.WriteNew(Path.Combine(source, ReceiptName), receipt);
        // A disconnect/unmount after verification must not authorize cleanup.
        verifyStorage?.Invoke();
        Cleanup(source);
    }

    private static void Cleanup(string source)
    {
        // Resolve every final deletion target before touching it. Do not follow directory links.
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        foreach (var file in files)
        {
            var resolved = Path.GetFullPath(file);
            if (!resolved.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Evidence cleanup escaped the attempt directory.");
            var relative = Path.GetRelativePath(source, resolved).Replace('\\', '/');
            if (relative == ReceiptName || Compact.Contains(relative)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Evidence cleanup refuses links.");
            File.Delete(file);
        }
        // Empty directories can stay: their removal is unnecessary for reclaiming disk space.
    }

    public static void Validate(string attemptDirectory)
    {
        var receipt = CampaignJson.Read<ArchivedAttempt>(Path.Combine(attemptDirectory, ReceiptName));
        ValidateArchive(attemptDirectory, receipt.ArchivePath, receipt.Sha256);
    }

    private static void ValidateArchive(string source, string archivePath, string expectedHash)
    {
        if (CampaignJson.HashFile(archivePath) != expectedHash) throw new InvalidDataException("Archived attempt SHA-256 mismatch.");
        var expected = CampaignJson.Read<Dictionary<string, string>>(Path.Combine(source, "sha256.json"));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) continue;
            if (Path.IsPathRooted(name) || name.Split('/', '\\').Contains("..") || seen.ContainsKey(name))
                throw new InvalidDataException("Unsafe or duplicate archive entry.");
            using var bytes = entry.Open();
            using var algorithm = SHA256.Create();
            seen[name] = Convert.ToHexString(algorithm.ComputeHash(bytes)).ToLowerInvariant();
        }
        // The seal itself is included in the archive but does not hash itself.
        if (!seen.Remove("sha256.json", out var sealHash) || sealHash != CampaignJson.HashFile(Path.Combine(source, "sha256.json")))
            throw new InvalidDataException("Archived attempt seal differs from compact evidence.");
        // HashTree excludes only a root logs/ directory. Keep its unsealed logs in the archive.
        foreach (var key in seen.Keys.Where(key => key.StartsWith("logs/", StringComparison.Ordinal)).ToArray()) seen.Remove(key);
        if (expected.Count != seen.Count || expected.Any(item => !seen.TryGetValue(item.Key, out var actual) || actual != item.Value))
            throw new InvalidDataException("Archived evidence does not match the original seal.");
        foreach (var name in Compact.Where(name => name != "sha256.json"))
            if (!seen.TryGetValue(name, out var hash) || hash != CampaignJson.HashFile(Path.Combine(source, name)))
                throw new InvalidDataException("Compact result differs from its archived original.");
        foreach (var remaining in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(source, remaining).Replace('\\', '/');
            if (name == ReceiptName || name == "sha256.json" || name == ".lock" || name.StartsWith("logs/")) continue;
            if (!seen.TryGetValue(name, out var hash) || CampaignJson.HashFile(remaining) != hash)
                throw new InvalidDataException("Remaining evidence differs from archived bytes.");
        }
    }
}
