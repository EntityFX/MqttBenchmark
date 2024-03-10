using EntityFX.MqttBenchmark.Bomber;
using EntityFX.MqttBenchmark.Bomber.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBomber.Configuration;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Sinks.InfluxDB;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

internal class Benchmark
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly MqttCounterClient mqttCounterClient;
    private readonly Settings settings;
    private readonly Dictionary<object, Dictionary<object, Dictionary<string, ScenarioParamsTemplate>>>[] scenarioGroups;

    private readonly Dictionary<string, long> scenarioReceiveCounetrs = new Dictionary<string, long>();
    private readonly Dictionary<string, ScenarioTimeStats> scenarioTimeStats = new Dictionary<string, ScenarioTimeStats>();

    private InfluxDBSink influxDbSink = new();

    public Benchmark(ILogger logger, IConfiguration configuration, MqttCounterClient mqttCounterClient)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.mqttCounterClient = mqttCounterClient;
        this.settings = configuration.Get<Settings>() ?? new Settings();
        scenarioGroups = GroupScenarioTemplates(settings.TestParams, settings.ScenarioNameTemplates);
    }

    public void Run()
    {
        Console.WriteLine($"Test set name: {this.settings.Name}");
        var configTemplateFile = File.ReadAllText(settings.TemplateFiles.GetValueOrDefault("Config", "config.template.json"));
        var scenariosTemplateFile = File.ReadAllText(settings.TemplateFiles.GetValueOrDefault("Scenarios", "scenarios.template.json"));

        var scenarioTemplates = JsonSerializer.Deserialize<Dictionary<string, ScenarioTemplate>>(scenariosTemplateFile);

        var startTimePath = DateTime.Now.ToString("s", CultureInfo.InvariantCulture).Replace(":", "_");
        var countersPath = Path.Combine("reports", this.settings.Name, startTimePath);
        var aggregatedReportSink = new AggregatedReportSink(scenarioReceiveCounetrs, scenarioTimeStats, countersPath);


        foreach (var scenarioGroup in scenarioGroups)
        {
            foreach (var scenarioSubGroup in scenarioGroup.Values)
            {
                foreach (var scenarioSubSubGroup in scenarioSubGroup.Values)
                {
                    var templates = scenarioSubSubGroup.Select(kv =>
                    {
                        var server = new Uri(kv.Value.Server);
                        var st = (ScenarioTemplate)scenarioTemplates[kv.Value.ScenarioTemplateName].Clone();
                        st.ScenarioName = kv.Key;
                        st.CustomSettings = new ScenarioTemplate.CustomSettingsTemplate()
                        {
                            Server = server.Host,
                            Port = server.Port,
                            Topic = kv.Value.Topic,
                            Qos = Convert.ToInt32(kv.Value.Params["qos"]),
                            ClientsCount = Convert.ToInt32(kv.Value.Params["clients"]),
                            MessageSize = Convert.ToInt32(kv.Value.Params.GetValueOrDefault("message", kv.Value.MessageSize))
                        };
                        st.LoadSimulationsSettings[0].KeepConstant[0] = st.CustomSettings.ClientsCount;
                        return st;
                    }).ToArray();

                    var scenarioParamsTemplate = scenarioSubSubGroup.FirstOrDefault().Value;

                    var firstTest = scenarioParamsTemplate.Group;

                    var configTemplateJson = JsonSerializer.Deserialize<JsonObject>(configTemplateFile);

                    var globalSettings = configTemplateJson["GlobalSettings"];
                    globalSettings["ScenariosSettings"] = JsonSerializer.SerializeToNode(templates);
                    globalSettings["TargetScenarios"] = JsonSerializer.SerializeToNode(scenarioSubSubGroup.Keys.ToArray());

                    var newTemplate = configTemplateJson.ToJsonString(new JsonSerializerOptions() { WriteIndented = true });

                    var fileName = $"config.{firstTest}.json";

                    File.WriteAllText(fileName, newTemplate);

                    aggregatedReportSink.AddScenarioSubGroup(scenarioSubSubGroup);

                    if (settings.InParallel)
                    {
                        RunParallel(startTimePath, aggregatedReportSink, scenarioSubSubGroup, scenarioParamsTemplate, firstTest, fileName);
                    }
                    else
                    {
                        RunSingle(startTimePath, aggregatedReportSink, scenarioSubSubGroup, scenarioParamsTemplate, fileName, GetScenarioReceiveCounetrs());
                    }


                }
            }
        }

        var resultsCsv = aggregatedReportSink.AsCsv();
        //var 
        File.WriteAllText(Path.Combine("reports", this.settings.Name, startTimePath, "results.csv"), resultsCsv);

        var resultsMd = aggregatedReportSink.AsMd();
        File.WriteAllText(Path.Combine("reports", this.settings.Name, startTimePath, "results.md"), resultsMd);

        Console.WriteLine(resultsMd);
    }

    private Dictionary<string, long> GetScenarioReceiveCounetrs()
    {
        return scenarioReceiveCounetrs;
    }

    private void RunSingle(string startTimePath, 
        AggregatedReportSink aggregatedReportSink, 
        Dictionary<string, ScenarioParamsTemplate> scenarioSubSubGroup, 
        ScenarioParamsTemplate scenarioParamsTemplate,
        string fileName, Dictionary<string, long> scenarioReceiveCounetrs)
    {
        foreach (var testItem in scenarioSubSubGroup)
        {
            NBomberRunner
                .RegisterScenarios(
                    new MqttScenarioBuilder(logger, configuration, scenarioReceiveCounetrs, scenarioTimeStats, mqttCounterClient)
                    .Build(testItem.Key)
                )
                .LoadInfraConfig("infra-config.json")
                .LoadConfig(fileName)
                .WithReportFileName(testItem.Key)
                .WithReportFolder(Path.Combine("reports", this.settings.Name, startTimePath, testItem.Key))
                .WithReportingSinks(aggregatedReportSink.WithScenarioParams(scenarioParamsTemplate))
                .Run();
        }
    }

    private void RunParallel(string startTimePath, 
        AggregatedReportSink aggregatedReportSink, 
        Dictionary<string, ScenarioParamsTemplate> scenarioSubSubGroup, 
        ScenarioParamsTemplate scenarioParamsTemplate,
        string firstTest, string fileName)
    {
        NBomberRunner
        .RegisterScenarios(
            scenarioSubSubGroup.Keys.Select(
                s => new MqttScenarioBuilder(
                    logger, configuration, scenarioReceiveCounetrs, scenarioTimeStats, mqttCounterClient).Build(s)).ToArray()
        )
        .LoadInfraConfig("infra-config.json")
        .LoadConfig(fileName)
        .WithReportFileName(firstTest)
        .WithReportFolder(Path.Combine("reports", this.settings.Name, startTimePath, firstTest))
        .WithReportingSinks(aggregatedReportSink.WithScenarioParams(scenarioParamsTemplate))
        .Run();
    }

    private Dictionary<object, Dictionary<object, Dictionary<string, ScenarioParamsTemplate>>>[] GroupScenarioTemplates(Dictionary<string, Dictionary<string, object>> testParams, Dictionary<string, ScenarioNameTemplate> scenarioNameTemplates)
    {
        return scenarioNameTemplates.Select(
            sn => GetMatchingCombination(sn.Key, sn.Value, testParams)
            .GroupBy(kv => kv.Value.Params[kv.Value.GroupBy])
            .ToDictionary(kv => kv.Key,
                kv => kv.GroupBy(kv1 => kv1.Value.Params[kv1.Value.SubGroupBy])
                .ToDictionary(kv2 => kv2.Key, kv2 => kv2.ToDictionary(kv3 => kv3.Key, kv3 => kv3.Value))
            )
            ).ToArray();
    }

    private Dictionary<string, ScenarioParamsTemplate> GetMatchingCombination(string replaceString, ScenarioNameTemplate value,
    Dictionary<string, Dictionary<string, object>> testParams)
    {
        var matchingParams = GetMatchingParams(replaceString, testParams);

        var outParams = new Dictionary<string, Dictionary<string, object>>();

        var allParams = matchingParams
            .Select(testParam => testParams[testParam]
                .Select(sc => (Group: testParam, Key: sc.Key, Value: sc.Value)).ToArray())
            .ToArray();

        var cr = allParams.Cartesian();

        var paramsCombinations = cr.OfType<IEnumerable<object>>().Select(
            p => p.OfType<(string Group, string Key, object Value)>().ToArray()
            ).ToArray();


        foreach (var paramscombination in paramsCombinations)
        {
            var resultstring = replaceString;
            foreach (var sub in paramscombination)
            {
                resultstring = resultstring.Replace($"{{{sub.Group}}}", sub.Value.ToString());
            }
            var paramsAsDir = paramscombination.ToDictionary(kv => kv.Group, kv => kv.Value);
            outParams.Add(resultstring, paramsAsDir);
        }

        var combination = outParams.ToDictionary(kv => kv.Key, kv => new ScenarioParamsTemplate()
        {
            Params = kv.Value,
            Group = ReplaceValueParams(value.Group, testParams, kv.Value),
            GroupBy = value.GroupBy,
            SubGroupBy = value.SubGroupBy,
            Server = ReplaceValueParams(value.Server, testParams, kv.Value),
            Topic = ReplaceValueParams(value.Topic, testParams, kv.Value),
            ScenarioTemplateName = value.ScenarioTemplateName,
            MessageSize = ReplaceValueParams(value.MessageSize, testParams, kv.Value),
        });

        return combination;
    }

    private string[] GetMatchingParams(string replaceString, Dictionary<string, Dictionary<string, object>> testParams)
    {
        return testParams.Where(testParam => replaceString.Contains($"{{{testParam.Key}}}"))
            .Select(testParam => testParam.Key).ToArray();
    }


    private string ReplaceValueParams(string replaceString, Dictionary<string, Dictionary<string, object>> testParams,
    Dictionary<string, object> paramsValues)
    {
        foreach (var testParam in testParams)
        {
            foreach (var testParamValue in testParam.Value)
            {
                if (replaceString.Contains($"{{{testParam.Key}:{testParamValue.Key}}}"))
                {
                    replaceString = replaceString.Replace($"{{{testParam.Key}:{testParamValue.Key}}}", testParamValue.Value.ToString());
                }


                foreach (var paramsValue in paramsValues)
                {
                    if (replaceString.Contains($"{{{testParam.Key}:{{{paramsValue.Key}}}}}"))
                    {
                        replaceString = replaceString.Replace($"{{{testParam.Key}:{{{paramsValue.Key}}}}}",
                            testParam.Value[paramsValue.Value.ToString()].ToString());
                    }
                }

            }
        }

        foreach (var paramsValue in paramsValues)
        {
            replaceString = replaceString.Replace($"{{{paramsValue.Key}}}", paramsValue.Value.ToString());
        }

        return replaceString;
    }
}