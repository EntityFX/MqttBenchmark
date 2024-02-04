using System.Security.Cryptography;

namespace EntityFX.MqttBenchmark.Bomber;

public static class StringHelper
{
    public static string GetString(int length) {
        using(var rng = new RNGCryptoServiceProvider()) {
            var bit_count = (length * 6);
            var byte_count = ((bit_count + 7) / 8); // rounded up
            var bytes = new byte[byte_count];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
    }
}