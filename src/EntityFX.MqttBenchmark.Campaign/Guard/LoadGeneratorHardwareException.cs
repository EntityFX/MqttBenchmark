namespace EntityFX.MqttBenchmark.Campaign;

public sealed class LoadGeneratorHardwareException : IOException
{
    public LoadGeneratorInterface Network { get; }
    public LoadGeneratorHardwareException(LoadGeneratorInterface network) : base("Routed adapter is not proven physical hardware.") => Network = network;
}
