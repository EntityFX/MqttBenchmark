using EntityFX.MqttBenchmark.Bomber;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBomber.CSharp;
using NBomber.Sinks.InfluxDB;


var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddCommandLine(args);

builder.Logging.ClearProviders();

var host = builder.Build();

var configuration = ScenarioHelper.InitConfiguration(host, args);
var logger = host.Services.GetRequiredService<ILogger<MqttScenarioBuilder>>();

var availableTests = configuration.GetSection("AvailableTests").Get<string[]>() ?? Array.Empty<string>();


InfluxDBSink influxDbSink = new();
var scenarioBuilder = new MqttScenarioBuilder(logger, configuration);

foreach (var testName in availableTests)
{
    NBomberRunner
    .RegisterScenarios(
        scenarioBuilder.Build(testName)
    )
    .LoadInfraConfig("config.json")
    .LoadConfig("config.json")
    .WithReportFileName(testName)
    .WithReportFolder(Path.Combine("reports", testName))
    //.WithReportingInterval(TimeSpan.FromSeconds(5))
    //.WithReportingSinks(influxDbSink)
    //.WithTestSuite("reporting")
    //.WithTestName("influx_db_demo")

    .Run();
}




