using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Helpers;

static class ResultsHelper
{
    public static string AsTable(IEnumerable<BenchmarkResults> results)
    {
        var sb = new StringBuilder();

        var headers = new Dictionary<string, int>()
        {
            ["Test"] = 18,
            ["Address"] = 25,
            ["Topic"] = 20,
            ["Qos"] = 3,
            ["Msg per sec"] = 18,
            ["Successes"] = 9,
            ["Failures"] = 8,
            ["Received"] = 8,
            ["Total bytes"] = 15,
            ["Total clients"] = 13,
            ["Total time"] = 10,
            ["Test time"] = 10,
            [$"Start DateTime"] = 19,
            [$"End DateTime"] = 19
        };

        var arrayResults = results.ToArray();

        foreach (BenchmarkResults r in arrayResults)
        {
            headers["Test"] = r.TestName.Length > headers["Test"] ? r.TestName.Length : headers["Test"];
            headers["Address"] = r.Settings.Broker!.ToString().Length > headers["Address"]
                ? r.Settings.Broker!.ToString().Length : headers["Address"];
            headers["Topic"] = r.Settings.Topic!.Length > headers["Topic"]
                ? r.Settings.Topic!.Length : headers["Topic"];
        }

        foreach (var headerItem in headers)
        {
            sb.Append($"| {headerItem.Key.PadLeft(headerItem.Value)} ");
        }
        sb.AppendLine("|");

        var dashes = headers.Select(d => new string('-', d.Value));


        sb.AppendLine($"|-{string.Join("-|-", dashes)}-|");

        foreach (var runResult in arrayResults)
        {
            var tr = runResult.TotalResults;
            var rowItems = new[]
            {
                $"{runResult.TestName.PadLeft(headers["Test"])}",
                $"{runResult.Settings.Broker!.ToString().PadLeft(headers["Address"])}",
                $"{runResult.Settings.Topic!.PadLeft(headers["Topic"])}",
                $"{runResult.Settings.Qos, 3}",
                $"{tr.MessagesPerSecond,18:N2}",
                $"{tr.Successes,9}",
                $"{tr.Failures,8}",
                $"{tr.Received,8}",
                $"{tr.TotalBytesSent,15:N0}",
                $"{runResult.ClientsCount,13:N0}",
                $"{tr.TotalPublishTime, 10:hh\\:mm\\:ss}",
                $"{tr.TestTime, 10:hh\\:mm\\:ss}",
                $"{runResult.StartDateTime, 19:s}",
                $"{runResult.EndDateTime, 19:s}",
            };
            sb.AppendLine($"| {string.Join(" | ", rowItems)} |");
        }

        return sb.ToString();
    }

    public static string AsCsv(IEnumerable<BenchmarkResults> results)
    {
        var sb = new StringBuilder();

        var headers = new[]
        {
            "Test", "Address", "Topic", "Qos",
            "Msg per sec",  "Successes", "Failures", "Received", "Total bytes",
            "Clients count", "Total publish time", "Test time",
            "Message min time", "Message max time", "Message mean",
            "Start DateTime", "End DateTime"
        };
        sb.AppendLine(string.Join(",", headers));

        foreach (var runResult in results)
        {
            var tr = runResult.TotalResults;
            var rowItems = new[]
            {
                $"{runResult.TestName}",
                $"{runResult.Settings.Broker}",
                $"{runResult.Settings.Topic}",
                $"{runResult.Settings.Qos}",
                FormattableString.Invariant($"{tr.MessagesPerSecond}"),
                $"{tr.Successes}",
                $"{tr.Failures}",
                $"{tr.Received}",
                $"{tr.TotalBytesSent}",
                $"{runResult.ClientsCount}",
                $"{tr.TotalPublishTime}",
                $"{tr.TestTime}",
                $"{tr.MessageTimeMin}",
                $"{tr.MessageTimeMax}",
                $"{tr.MessageTimeMeanAvg}",
                $"{runResult.StartDateTime}",
                $"{runResult.EndDateTime}"
            };
            sb.AppendLine(string.Join(",", rowItems));
        }

        return sb.ToString();
    }

    public static void StoreResults(BenchmarkResults benchmarkResults, string testName,
        Settings settings, string testResultsOutputPath)
    {
        var jsonSerializerOptions = new JsonSerializerOptions() { WriteIndented = true };

        var totalResultsJsonString = JsonSerializer.Serialize(benchmarkResults.TotalResults, jsonSerializerOptions);
        var runResultsJson = JsonSerializer.Serialize(benchmarkResults.RunResults, jsonSerializerOptions);
        var settingJsonString = JsonSerializer.Serialize(settings, jsonSerializerOptions);

        var testOutputPath = Path.Combine(testResultsOutputPath, testName);
        if (!Directory.Exists(testOutputPath))
        {
            Directory.CreateDirectory(testOutputPath);
        }

        var totalOutputPath = Path.Combine(testOutputPath, "total-results.json");
        var resultsOutputPath = Path.Combine(testOutputPath, "run-results.json");
        var settingsOutputPath = Path.Combine(testOutputPath, "settings.json");
        File.WriteAllText(totalOutputPath, totalResultsJsonString);
        File.WriteAllText(resultsOutputPath, runResultsJson);
        File.WriteAllText(settingsOutputPath, settingJsonString);
    }
}