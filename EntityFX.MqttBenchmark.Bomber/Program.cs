using EntityFX.MqttBenchmark.Bomber;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var appSettingsPath = GetExtraConfig(args) ?? "appsettings.json";
var appSettingsFileName = Path.GetFileNameWithoutExtension(appSettingsPath);

builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile(appSettingsPath)
    .AddJsonFile($"{appSettingsFileName}.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

Console.WriteLine($"Use config: {appSettingsPath}");
Console.WriteLine($"Envrionment: {builder.Environment.EnvironmentName}");


builder.Logging.ClearProviders();

var host = builder.Build();

var configuration = ScenarioHelper.InitConfiguration(host, args);
var logger = host.Services.GetRequiredService<ILogger<MqttScenarioBuilder>>();

var benchmark = new Benchmark(logger, configuration);
benchmark.Run();

string? GetExtraConfig(string[] args)
{
    var configArgNames = new[] { "-c", "--config" };

    for (int i = 0; i < args.Length; i++)
    {
        if (configArgNames.Contains(args[i]) && i <= args.Length)
        {
            return args[++i];
        }
    }

    return null;
}