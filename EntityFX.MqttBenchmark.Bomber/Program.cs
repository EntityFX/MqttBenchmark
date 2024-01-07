using EntityFX.MqttBenchmark.Bomber;
using EntityFX.MqttBenchmark.Bomber.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBomber.Configuration;
using NBomber.CSharp;
using NBomber.Sinks.InfluxDB;
using System.Collections;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddCommandLine(args);

builder.Logging.ClearProviders();

var host = builder.Build();

var configuration = ScenarioHelper.InitConfiguration(host, args);
var logger = host.Services.GetRequiredService<ILogger<MqttScenarioBuilder>>();

var testParams = configuration.GetSection("TestParams").Get<Dictionary<string, Dictionary<string, object>>>()
    ?? new Dictionary<string, Dictionary<string, object>>();

var scenarioNameTemplates = configuration.GetSection("ScenarioNameTemplates").Get<Dictionary<string, ScenarioNameTemplate>>()
    ?? new Dictionary<string, ScenarioNameTemplate>();

var scenarioGroups = GroupScenarioTemplates(testParams, scenarioNameTemplates);

var configTemplateFile = File.ReadAllText("config.template.json");

var scenariosTemplateFile = File.ReadAllText("scenarios.template.json");
var scenarioTemplates = JsonSerializer.Deserialize<Dictionary<string, ScenarioTemplate>>(scenariosTemplateFile);

InfluxDBSink influxDbSink = new();


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
                    ClientsCount = Convert.ToInt32(kv.Value.Params["clients"])
                };
                st.LoadSimulationsSettings[0].KeepConstant[0] = st.CustomSettings.ClientsCount;
                return st;
            }).ToArray();

            var firstTest = scenarioSubSubGroup.FirstOrDefault().Value.Group;

            var configTemplateJson = JsonSerializer.Deserialize<JsonObject>(configTemplateFile);

            var globalSettings = configTemplateJson["GlobalSettings"];
            globalSettings["ScenariosSettings"] = JsonSerializer.SerializeToNode(templates);
            globalSettings["TargetScenarios"] = JsonSerializer.SerializeToNode(scenarioSubSubGroup.Keys.ToArray());

            var newTemplate = configTemplateJson.ToJsonString(new JsonSerializerOptions() { WriteIndented = true });

            var fileName = $"config.{firstTest}.json";

            File.WriteAllText(fileName, newTemplate);

            NBomberRunner
                .RegisterScenarios(
                    scenarioSubSubGroup.Keys.Select(s => new MqttScenarioBuilder(logger, configuration).Build(s)).ToArray()
                )
                .LoadInfraConfig("infra-config.json")
                .LoadConfig(fileName)
                .WithReportFileName(firstTest)
                .WithReportFolder(Path.Combine("reports", firstTest))
                .Run();
        }
    }
}



string ReplaceParams(string replaceString, Dictionary<string, object[]> testParams, out Dictionary<string, object> outParams)
{
    outParams = new Dictionary<string, object>();
    foreach (var testParam in testParams)
    {
        foreach (var testParamValue in testParam.Value)
        {
            if (replaceString.Contains($"{{{testParam.Key}}}"))
            {
                outParams.Add(testParam.Key, testParamValue);
            }
            replaceString = replaceString.Replace($"{{{testParam.Key}}}", testParamValue.ToString());
        }
    }

    return replaceString;
}

string ReplaceValueParams(string replaceString, Dictionary<string, Dictionary<string, object>> testParams,
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

string[] GetMatchingParams(string replaceString, Dictionary<string, Dictionary<string, object>> testParams)
{

    return testParams.Where(testParam => replaceString.Contains($"{{{testParam.Key}}}"))
        .Select(testParam => testParam.Key).ToArray();
}

Dictionary<string, ScenarioParamsTemplate> GetMatchingCombination(string replaceString, ScenarioNameTemplate value,
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
        ScenarioTemplateName = value.ScenarioTemplateName
    });

    return combination;
}

Dictionary<object, Dictionary<object, Dictionary<string, ScenarioParamsTemplate>>>[] GroupScenarioTemplates(Dictionary<string, Dictionary<string, object>> testParams, Dictionary<string, ScenarioNameTemplate> scenarioNameTemplates)
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