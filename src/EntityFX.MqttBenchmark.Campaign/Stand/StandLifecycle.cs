using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Жизненный цикл stand-контроллера: вызовы PowerShell (pull/validate/start/health/clock/stop),
/// определение и удержание Docker-context lease, фиксация controller-артефактов.
/// Не знает о телеметрии.
/// </summary>
public sealed class StandLifecycle
{
    private readonly string script, selectedPath, output, docker;
    private readonly string? trustedBuildProvenanceDirectory;
    private readonly Dictionary<string, FileStream> leases = new(StringComparer.Ordinal);
    private bool started;

    public StandLifecycle(string script, string selectedPath, string output, string dockerExecutable,
        string? trustedBuildProvenanceDirectory)
    {
        this.script = script;
        this.selectedPath = selectedPath;
        this.output = output;
        docker = dockerExecutable;
        this.trustedBuildProvenanceDirectory = trustedBuildProvenanceDirectory;
    }

    public bool Started => started;

    /// <summary>Снимает Docker-context lease для указанного контекста (идемпотентно).</summary>
    public void LeaseContext(string context)
    {
        if (leases.ContainsKey(context)) return;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        leases.Add(context, new FileStream(Path.Combine(Path.GetTempPath(), "mqttbenchmark-stand-" + id + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public void MarkStarted() => started = true;

    /// <summary>Сбрасывает признак запущенного стенда (после остановки).</summary>
    public void MarkStopped() => started = false;

    public void Release()
    {
        foreach (var lease in leases.Values) lease.Dispose();
        leases.Clear();
    }

    /// <summary>Контекст контроллера: при distributedHosts — контекст выбранного брокера, иначе корневой.</summary>
    /// <summary>
    /// Имя исполняемого файла PowerShell 7 (pwsh). По умолчанию <c>pwsh</c>; переопределяется
    /// переменной окружения <c>MQB_STAND_SHELL</c> (например, полный путь) — переносимо на
    /// Windows и Linux, где pwsh может отсутствовать в PATH.
    /// </summary>
    internal static string StandShell
    {
        get
        {
            var shell = Environment.GetEnvironmentVariable("MQB_STAND_SHELL");
            return string.IsNullOrWhiteSpace(shell) ? "pwsh" : shell;
        }
    }

    public static string ControllerContext(JsonNode inventory, JsonNode selected)
    {
        var context = inventory["deploymentMode"]?.GetValue<string>() == "distributedHosts"
            ? selected["dockerContext"]?.GetValue<string>() : inventory["dockerContext"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(context) ? context : throw new InvalidDataException("The controller's selected Docker context is required.");
    }

    public async Task InvokeAsync(string action, string inventory, string directory, int seconds, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        // Reserve before launching: the controller legitimately writes its own output files.
        // A second invocation must fail before it can overwrite those files or append telemetry.
        CampaignJson.WriteNew(Path.Combine(directory, "controller-" + action + "-started.json"),
            new { action, startedAtUtc = DateTimeOffset.UtcNow });
        var info = new ProcessStartInfo(StandShell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-Action", action, "-ConfigPath", inventory,
            "-OutputDirectory", directory, "-DockerExecutable", docker, "-CaptureSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(arg);
        if (trustedBuildProvenanceDirectory != null)
        {
            info.ArgumentList.Add("-TrustedBuildProvenanceDirectory");
            info.ArgumentList.Add(trustedBuildProvenanceDirectory);
        }
        using var process = Process.Start(info) ?? throw new IOException("Unable to launch stand controller.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        var result = new { action, exitCode = process.ExitCode, stdout = await stdout, stderr = await stderr };
        CampaignJson.WriteNew(Path.Combine(directory, "controller-" + action + ".json"), result);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Stand controller {action} failed: {result.stderr.Trim()}");
    }
}