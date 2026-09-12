namespace EntityFX.MqttBenchmark.Campaign;

public interface IWindowsAdapterMetadataProvider
{
    AdapterHardwareEvidence Read(int interfaceIndex);
}
