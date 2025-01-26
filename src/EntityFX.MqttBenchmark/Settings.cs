namespace EntityFX.MqttBenchmark;

public class Settings : ICloneable
{
    public Uri? Broker { get; set; }

    public string? Topic { get; set; }

    public string? Payload { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public int? Qos { get; set; }

    public TimeSpan? PublishTimeout { get; set; }

    public int? MessageSize { get; set; }

    public int? MessageCount { get; set; }

    public TimeSpan? TestMaxTime { get; set; }

    public TimeSpan? WaitAfterTime { get; set; }

    public int? ConnectAttempts { get; set; }

    public int? Clients { get; set; }

    public string? ClientPrefix { get; set; }

    public string? ClientCert { get; set; }

    public string? ClientKey { get; set; }

    public string? BrokerCaCert { get; set; }

    public bool? Insecure { get; set; } = false;

    public TimeSpan? MessageDelayInterval { get; set; } = TimeSpan.FromMilliseconds(2);

    public int? MaxFailure { get; set; } = 1000;

    public object Clone()
    {
        return MemberwiseClone();
    }
}