using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Потокобезопасный реестр публикаций и доставок одного прогона. Контракт:
/// каждая публикация получает ровно один вызов <see cref="Attempt"/> и ровно одно завершение
/// (<see cref="Complete"/> или <see cref="Fail"/>); доставки приходят по подписанному идентификатору
/// и могут повторяться (дубликаты) или приходить для неизвестных идентификаторов (неожиданные).
/// Итоги снимаются только после урегулирования всех публикаций.
/// </summary>
public sealed class DeliveryLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, Outcome> publishes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> deliveries = new(StringComparer.Ordinal);
    private double lastDelivery;
    private sealed record Outcome(bool? Completed, double Latency, string? Error);

    /// <summary>Регистрирует попытку публикации с уникальным идентификатором.</summary>
    /// <exception cref="InvalidOperationException">Идентификатор уже зарегистрирован.</exception>
    public void Attempt(string id)
    {
        lock (gate) if (!publishes.TryAdd(id, new(null, 0, null)))
            throw new InvalidOperationException("Duplicate publish identity.");
    }

    /// <summary>Помечает публикацию успешно завершённой с латентностью (мс).</summary>
    /// <exception cref="InvalidOperationException">Публикация не зарегистрирована или уже завершена.</exception>
    public void Complete(string id, double latencyMs) => SetOutcome(id, new(true, latencyMs, null));

    /// <summary>Помечает публикацию неуспешной с причиной.</summary>
    /// <exception cref="InvalidOperationException">Публикация не зарегистрирована или уже завершена.</exception>
    public void Fail(string id, string reason) => SetOutcome(id, new(false, 0, reason));

    private void SetOutcome(string id, Outcome value)
    {
        lock (gate)
        {
            if (!publishes.TryGetValue(id, out var previous) || previous.Completed != null)
                throw new InvalidOperationException("Publish must have exactly one attempt and one outcome.");
            publishes[id] = value;
        }
    }

    /// <summary>Регистрирует доставку по идентификатору публикации; повторные вызовы — дубликаты.</summary>
    /// <param name="seconds">Монотонное время доставки (для последней активности drain).</param>
    public void Deliver(string id, double seconds)
    {
        lock (gate)
        {
            deliveries[id] = deliveries.GetValueOrDefault(id) + 1;
            lastDelivery = Math.Max(lastDelivery, seconds);
        }
    }

    /// <summary>Состояние drain: все публикации урегулированы и каждая успешная получила доставку;
    /// плюс время последней доставки (0, если доставок не было).</summary>
    public (bool AllDelivered, double LastDelivery) DrainState()
    {
        lock (gate) return (publishes.Values.All(x => x.Completed != null) &&
            publishes.Where(x => x.Value.Completed == true).All(x => deliveries.ContainsKey(x.Key)), lastDelivery);
    }

    /// <summary>
    /// Снимает итоговую сводку. Требует, чтобы все публикации были урегулированы.
    /// Для QoS 0 латентность не применима; для QoS 1/2 латентность считается по успешно завершённым.
    /// </summary>
    /// <param name="qos">Уровень QoS (0–2); определяет применимость латентности.</param>
    /// <param name="actualSeconds">Наблюдаемая длительность измерения (положительная, конечная).</param>
    /// <exception cref="InvalidDataException">Недопустимый QoS или длительность.</exception>
    /// <exception cref="InvalidOperationException">Есть неурегулированные публикации.</exception>
    public MeasurementSummary Snapshot(int qos, double actualSeconds)
    {
        if (qos is < 0 or > 2 || !double.IsFinite(actualSeconds) || actualSeconds <= 0)
            throw new InvalidDataException("A summary requires valid QoS and positive observed measurement duration.");
        lock (gate)
        {
            if (publishes.Values.Any(x => x.Completed == null)) throw new InvalidOperationException("Unsettled publishes.");
            var completed = publishes.Where(x => x.Value.Completed == true).ToArray();
            var failed = publishes.Where(x => x.Value.Completed == false).ToArray();
            return new(publishes.Count, completed.Length, failed.Length,
                deliveries.Keys.LongCount(publishes.ContainsKey), deliveries.Values.Sum(x => x - 1),
                deliveries.Where(x => !publishes.ContainsKey(x.Key)).Sum(x => x.Value),
                failed.LongCount(x => deliveries.ContainsKey(x.Key)), completed.LongCount(x => deliveries.ContainsKey(x.Key)),
                actualSeconds, qos == 0 || completed.Length == 0 ? null : LatencyQuantiles.FromSamples(completed.Select(x => x.Value.Latency)),
                qos == 0 ? "notApplicable" : completed.Length == 0 ? "unobserved" : "observed",
                failed.GroupBy(x => x.Value.Error ?? "unknown").ToDictionary(x => x.Key, x => x.LongCount()));
        }
    }
}
