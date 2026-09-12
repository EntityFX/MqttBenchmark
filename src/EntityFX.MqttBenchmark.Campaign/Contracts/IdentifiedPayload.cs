using System.Security.Cryptography;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Формирует/читает полезные нагрузки с восстанавливаемым идентификатором: первые 16 байт тела —
/// старшие 128 бит SHA-256 от (campaign, run, publisher, sequence), остаток заполняется тем же
/// хешем. Идентификатор достаётся из первых 16 байт без внешнего контекста.
/// </summary>
public static class IdentifiedPayload
{
    /// <summary>Создаёт полезную нагрузку заданного размера с встроенным идентификатором.</summary>
    /// <param name="size">Размер тела, не менее 16 байт.</param>
    /// <param name="campaign">Идентификатор кампании.</param>
    /// <param name="run">Идентификатор прогона.</param>
    /// <param name="publisher">Номер издателя (неотрицательный).</param>
    /// <param name="sequence">Последовательный номер (неотрицательный).</param>
    /// <returns>Массив байт; первые 16 байт — восстанавливаемый идентификатор.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> меньше 16.</exception>
    /// <exception cref="ArgumentException">Пустой campaign/run либо отрицательные publisher/sequence.</exception>
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
    /// <summary>Извлекает идентификатор из первых 16 байт полезной нагрузки.</summary>
    /// <param name="payload">Тело сообщения.</param>
    /// <returns>Нижний регистр hex-идентификатора (32 символа).</returns>
    /// <exception cref="InvalidDataException">Тело короче 16 байт.</exception>
    public static string ReadId(ReadOnlySpan<byte> payload) => payload.Length >= 16
        ? Convert.ToHexString(payload[..16]).ToLowerInvariant()
        : throw new InvalidDataException("Payload does not contain the 16-byte identity.");
}
