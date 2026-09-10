using System.Security.Cryptography;
using System.Text;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record CampaignKey(string Broker, int MessageBytes, int Qos, int Publishers, int Repeat)
{
    public string Key => $"{Broker}.m{MessageBytes}.q{Qos}.p{Publishers}.r{Repeat}";
}

public sealed class CampaignDefinition
{
    public int SchemaVersion { get; set; } = 3;
    public int Seed { get; set; } = 20260909;
    public string[] Brokers { get; set; } = { "Aedes", "Mosquitto", "ActiveMQ", "EMQX" };
    public int[] MessageBytes { get; set; } = { 16, 256 };
    public int[] Qos { get; set; } = { 0, 1, 2 };
    public int[] Publishers { get; set; } = { 1, 16, 64, 128 };
    public int Repeats { get; set; } = 3;
    public double WarmupSeconds { get; set; } = 5;
    public double MeasurementSeconds { get; set; } = 30;
    public double CooldownSeconds { get; set; } = 5;
    public double DrainTimeoutSeconds { get; set; } = 30;
    public double QuietPeriodSeconds { get; set; } = 2;
    public string DeploymentMode { get; set; } = "singleHostSequential";
    public string CpuMode { get; set; } = "singleCorePinned";
    public Dictionary<string, double> NetworkThresholdPercentByBroker { get; set; } = new(StringComparer.Ordinal)
    {
        ["Aedes"] = 85,
        ["Mosquitto"] = 80,
        ["ActiveMQ"] = 80,
        ["EMQX"] = 80
    };

    public double NetworkThresholdPercentFor(string broker)
    {
        Validate();
        return NetworkThresholdPercentByBroker[broker];
    }
    public CampaignKey[] Expand()
    {
        Validate();
        // SHA-256 ordering is stable across runtime versions. Group brokers to keep the stand sequential.
        return (from broker in Brokers.OrderBy(Order, StringComparer.Ordinal)
                from key in (from bytes in MessageBytes from qos in Qos from publishers in Publishers
                             from repeat in Enumerable.Range(1, Repeats)
                             select new CampaignKey(broker, bytes, qos, publishers, repeat))
                            .OrderBy(x => Order(x.Key), StringComparer.Ordinal)
                select key).ToArray();
    }

    public void Validate()
    {
        static bool Same<T>(IEnumerable<T>? actual, T[] expected) => actual != null &&
            actual.Count() == expected.Length && actual.ToHashSet().SetEquals(expected);
        if (SchemaVersion != 3 || Seed != 20260909 || Repeats != 3 ||
            !Same(Brokers, new[] { "Aedes", "Mosquitto", "ActiveMQ", "EMQX" }) ||
            !Same(MessageBytes, new[] { 16, 256 }) || !Same(Qos, new[] { 0, 1, 2 }) ||
            !Same(Publishers, new[] { 1, 16, 64, 128 }))
            throw new InvalidDataException("Campaign v3 requires the approved 288 keys and seed 20260909.");
        if (new[] { WarmupSeconds, MeasurementSeconds, CooldownSeconds, DrainTimeoutSeconds, QuietPeriodSeconds }
            .Any(x => !double.IsFinite(x) || x < 0) || MeasurementSeconds <= 0 || QuietPeriodSeconds <= 0 ||
            DrainTimeoutSeconds < QuietPeriodSeconds)
            throw new InvalidDataException("Campaign durations must be finite and the drain timeout must cover the quiet period.");
        if (DeploymentMode is not ("singleHostSequential" or "distributedHosts") ||
            CpuMode is not ("singleCorePinned" or "hostAllCores"))
            throw new InvalidDataException("Unsupported campaign deployment/CPU mode.");
        if (NetworkThresholdPercentByBroker == null || NetworkThresholdPercentByBroker.Count != Brokers.Length ||
            Brokers.Any(broker => !NetworkThresholdPercentByBroker.TryGetValue(broker, out var threshold) ||
                !double.IsFinite(threshold) || threshold <= 0 || threshold > 100) ||
            NetworkThresholdPercentByBroker.Keys.Any(broker => !Brokers.Contains(broker, StringComparer.Ordinal)))
            throw new InvalidDataException("Every campaign broker requires one finite network threshold in (0, 100].");
    }

    private string Order(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Seed}|{key}")));
}
