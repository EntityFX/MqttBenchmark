namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Описание среды исполнения генератора нагрузки (ОС, CPU, рантайм, частота стоп-часов).</summary>
public sealed record BenchmarkEnvironment(string OsDescription, int ProcessorCount, string FrameworkDescription,
    string Architecture, string MachineName, long StopwatchFrequency, DateTimeOffset CapturedAtUtc);
