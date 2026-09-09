using System.Diagnostics;

namespace EntityFX.MqttBenchmark.Calibration;

public static class GitRevisionReader
{
    public static string ReadHead(string repositoryPath)
    {
        var fullPath = Path.GetFullPath(repositoryPath);
        var status = Run(fullPath, "status", "--porcelain", "--untracked-files=no");
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException(
                $"Git repository has tracked changes and cannot provide reproducible provenance: '{fullPath}'.");
        var output = Run(fullPath, "rev-parse", "HEAD").Trim();
        if (output.Length != 40 || output.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException($"Git HEAD is invalid for '{fullPath}'.");
        return output.ToLowerInvariant();
    }

    private static string Run(string repositoryPath, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Unable to start git for provenance capture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Unable to inspect git repository '{repositoryPath}': {error}");
        return output;
    }
}
