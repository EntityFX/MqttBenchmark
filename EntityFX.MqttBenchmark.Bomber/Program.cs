using EntityFX.MqttBenchmark.Bomber;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddCommandLine(args);

builder.Logging.ClearProviders();

var host = builder.Build();

var configuration = ScenarioHelper.InitConfiguration(host, args);
var logger = host.Services.GetRequiredService<ILogger<MqttScenarioBuilder>>();

var benchmark = new Benchmark(logger, configuration);
benchmark.Run();