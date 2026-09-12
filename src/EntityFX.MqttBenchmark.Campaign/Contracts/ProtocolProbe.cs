namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Одна RTT-эхо проба preflight: результат CONNECT/SUBSCRIBE/PUBLISH/доставки на уровне QoS
/// и описание ошибки при неуспехе.</summary>
public sealed record ProtocolProbe(int Qos, bool Connect, bool Subscribe, bool Publish, bool Delivered, string? Error);
