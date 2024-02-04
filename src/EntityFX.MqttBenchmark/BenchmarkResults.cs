namespace EntityFX.MqttBenchmark;

record BenchmarkResults(
    string TestName, int ClientsCount, TotalResults TotalResults, 
    RunResults[] RunResults, DateTimeOffset StartDateTime,
    DateTimeOffset EndDateTime, Settings Settings);