using System.Diagnostics;

namespace EntityFX.MqttBenchmark.Calibration;

public static class GitRevisionReader
{
    public static string ReadHead(string repositoryPath)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Path.GetFullPath(repositoryPath));
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Unable to start git for provenance capture.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length != 40 || output.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException(
                $"Unable to read git HEAD from '{repositoryPath}': {error}");
        return output.ToLowerInvariant();
    }
}
