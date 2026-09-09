using System.Security.Cryptography;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public static class IdentifiedPayload
{
    public static byte[] Create(int size, string campaign, string run, int publisher, long sequence)
    {
        if (size < 16) throw new ArgumentOutOfRangeException(nameof(size));
        if (string.IsNullOrWhiteSpace(campaign) || string.IsNullOrWhiteSpace(run) || publisher < 0 || sequence < 0)
            throw new ArgumentException("Payload identity requires campaign, run, nonnegative publisher and sequence.");
        // Length-delimited JSON prevents ambiguous identities. First 128 hash bits ARE the recoverable ID.
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[] { campaign, run, publisher, sequence }));
        var payload = new byte[size];
        for (var i = 0; i < size; i++) payload[i] = hash[i % hash.Length];
        return payload;
    }
    public static string ReadId(ReadOnlySpan<byte> payload) => payload.Length >= 16
        ? Convert.ToHexString(payload[..16]).ToLowerInvariant()
        : throw new InvalidDataException("Payload does not contain the 16-byte identity.");
}
