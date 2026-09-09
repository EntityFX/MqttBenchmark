using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityFX.MqttBenchmark.Calibration;

public static class BenchmarkPayload
{
    public static byte[] Create(int messageBytes, int seed)
    {
        if (messageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(messageBytes));
        var result = new byte[messageBytes];
        var offset = 0;
        var block = 0;
        Span<byte> input = stackalloc byte[8];
        while (offset < result.Length)
        {
            BinaryPrimitives.WriteInt32BigEndian(input[..4], seed);
            BinaryPrimitives.WriteInt32BigEndian(input[4..], block++);
            var hash = SHA256.HashData(input);
            var count = Math.Min(hash.Length, result.Length - offset);
            hash.AsSpan(0, count).CopyTo(result.AsSpan(offset));
            offset += count;
        }
        return result;
    }
}
