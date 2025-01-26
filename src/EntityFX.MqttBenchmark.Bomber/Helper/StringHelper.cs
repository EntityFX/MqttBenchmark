using System.Security.Cryptography;

namespace EntityFX.MqttBenchmark.Bomber;

public static class StringHelper
{
    public static string GetString(int length) {
        var bit_count = length * 6;
        var byte_count = (bit_count + 7) / 8; // rounded up
        var bytes = RandomNumberGenerator.GetBytes(byte_count);
        return Convert.ToBase64String(bytes);
    }
}