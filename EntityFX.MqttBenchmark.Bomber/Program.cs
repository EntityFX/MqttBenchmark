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


InfluxDBSink influxDbSink = new();
var scenario1 = new MqttScenarioBuilder(logger, configuration);
//var scenario2 = new MqttScenarioBuilder(logger, configuration);


NBomberRunner
    .RegisterScenarios(
        scenario1.Build("serialize_publish_qos0")/*,
        scenario2.Build("serialize_publish_qos1")*/
    )
    .LoadInfraConfig("config.json")
    .LoadConfig("config.json")
    //.WithReportingInterval(TimeSpan.FromSeconds(5))
    //.WithReportingSinks(influxDbSink)
    //.WithTestSuite("reporting")
    //.WithTestName("influx_db_demo")

    .Run();
