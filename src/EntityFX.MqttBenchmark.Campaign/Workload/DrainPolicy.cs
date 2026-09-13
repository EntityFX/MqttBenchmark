namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Политика завершения drain-фазы: продолжаем ожидание доставок, пока они идут, и прекращаем
/// при полной доставке, превышении таймаута или тишине (quiet period) после последней доставки.
/// </summary>
public static class DrainPolicy
{
    /// <summary>
    /// Принимает решение о завершении drain на текущий момент времени.
    /// </summary>
    /// <param name="ledger">Реестр публикаций/доставок прогона.</param>
    /// <param name="start">Монотонное время начала drain.</param>
    /// <param name="now">Текущее монотонное время.</param>
    /// <param name="quiet">Период тишины: прекращаем, если после последней доставки прошло больше.</param>
    /// <param name="timeout">Жёсткий таймаут drain от <paramref name="start"/>.</param>
    /// <returns>Причина завершения: <c>allCompletedDelivered</c>, <c>timeout</c>, <c>quietPeriod</c>,
    /// либо <c>null</c> — продолжаем ожидание.</returns>
    public static string? Decide(DeliveryLedger ledger, double start, double now, double quiet, double timeout)
    {
        var state = ledger.DrainState();
        if (state.AllDelivered) return "allCompletedDelivered";
        if (now - start >= timeout) return "timeout";
        if (now - Math.Max(start, state.LastDelivery) >= quiet) return "quietPeriod";
        return null;
    }
}
