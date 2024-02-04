
namespace EntityFX.MqttBenchmark;

record TotalResults(
    decimal Ratio,
    long Successes,
    long Failures,
    long Received,
    TimeSpan TotalPublishTime,
    TimeSpan TestTime,
    TimeSpan AverageRunTime,
    TimeSpan MessageTimeMin,
    TimeSpan MessageTimeMax,
    TimeSpan MessageTimeMeanAvg,
    decimal MessageTimeStandardDeviation,
    decimal MessagesPerSecond,
    decimal AverageMessagesPerSec,
    long TotalBytesSent
)
{
    public long Received { get; set; } = Received;
}