namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Журнал исполнения кампании: резервирует каталог результатов эксклюзивным lock-файлом,
/// сохраняет каждую попытку как неизменяемый набор файлов (<c>started.json</c>, <c>result.json</c>,
/// <c>sha256.json</c>) и позволяет возобновить прерванный прогон с того же входа (--resume).
///
/// Контракты целостности:
/// — одна кампания на каталог; запись возможна только под удержанным lock;
/// — попытки нумеруются подряд (1..N) и не перезаписываются;
/// — прерванная попытка (без <c>sha256.json</c>) считается неуспешной и сохраняет байты для анализа;
/// — успешный ключ матрицы больше не выполняется.
/// </summary>
public sealed class CampaignJournal : IDisposable
{
    private readonly string root;
    private readonly CampaignIdentity identity;
    private readonly FileStream lease;
    private CampaignJournal(string root, CampaignIdentity identity, FileStream lease) => (this.root, this.identity, this.lease) = (root, identity, lease);

    /// <summary>
    /// Открывает каталог кампании и захватывает эксклюзивный lock (<c>.lock</c>, <see cref="FileShare.None"/>),
    /// который удерживается до <see cref="Dispose"/>.
    /// </summary>
    /// <param name="directory">Каталог результатов кампании; будет создан при отсутствии.</param>
    /// <param name="identity">Неизменяемая идентичность кампании (конфиг, стенд, репозиторий).</param>
    /// <param name="resume"><c>true</c> — продолжить прерванный прогон: манифест <c>campaign.json</c> обязан
    /// совпасть с <paramref name="identity"/>; <c>false</c> — новый прогон: каталог обязан быть пустым
    /// (кроме <c>.lock</c>), после чего в него записывается манифест.</param>
    /// <returns>Открытый журнал, владеющий lock-файлом.</returns>
    /// <exception cref="IOException">Каталог не пуст при новом прогоне, либо lock занят другим процессом.</exception>
    /// <exception cref="InvalidDataException">Идентичность <paramref name="identity"/> не совпадает с манифестом
    /// или с ранее записанными попытками (повреждённая/чужая история).</exception>
    public static CampaignJournal Open(string directory, CampaignIdentity identity, bool resume)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var lease = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var manifest = Path.Combine(root, "campaign.json");
            if (resume)
            {
                if (CampaignJson.Read<CampaignIdentity>(manifest) != identity)
                    throw new InvalidDataException("Resume identity differs from immutable campaign manifest.");
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(root).Any(x => Path.GetFileName(x) != ".lock"))
                    throw new IOException("Campaign directory already exists; use --resume with the same inputs.");
                CampaignJson.WriteNew(manifest, identity);
            }
            var rows = ReadAttempts(root);
            if (rows.Any(x => x.Identity != identity)) throw new InvalidDataException("Attempt belongs to a different campaign identity.");
            return new(root, identity, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>
    /// Читает полную историю попыток из каталога кампании без захвата lock
    /// (используется агрегатором и командами для предварительного анализа).
    /// </summary>
    /// <param name="directory">Каталог результатов кампании.</param>
    /// <returns>Список попыток в порядке обхода ключей и номеров попыток. Прерванные попытки
    /// возвращаются со статусом <c>interrupted</c> и причиной <c>"Attempt was not sealed."</c>.</returns>
    /// <exception cref="InvalidDataException">История попыток не подряд (пропущен номер), выходит за
    /// ожидаемый формат, либо хеш-верификация <c>sha256.json</c> не прошла (файлы изменены после записи).</exception>
    public static IReadOnlyList<AttemptResult> ReadAttempts(string directory)
    {
        var rows = new List<AttemptResult>();
        var attemptsRoot = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsRoot)) return rows;
        foreach (var keyDirectory in Directory.EnumerateDirectories(attemptsRoot))
        {
            var previous = 0;
            foreach (var attemptDirectory in Directory.EnumerateDirectories(keyDirectory).OrderBy(x => x, StringComparer.Ordinal))
            {
                var start = CampaignJson.Read<AttemptResult>(Path.Combine(attemptDirectory, "started.json"));
                if (start.Attempt != ++previous || start.Attempt > 3 || Path.GetFileName(keyDirectory) != start.Key.Key ||
                    Path.GetFileName(attemptDirectory) != $"attempt-{start.Attempt:00}" || start.Status != "started")
                    throw new InvalidDataException("Invalid or non-contiguous attempt history.");
                var resultPath = Path.Combine(attemptDirectory, "result.json");
                var sealPath = Path.Combine(attemptDirectory, "sha256.json");
                // A crash before sealing consumes an attempt and preserves every byte for investigation.
                if (!File.Exists(sealPath)) { rows.Add(start with { Status = "interrupted", Failure = "Attempt was not sealed." }); continue; }
                var expected = CampaignJson.Read<Dictionary<string, string>>(sealPath);
                var actual = CampaignJson.HashTree(attemptDirectory).Where(x => x.Key != "sha256.json").ToDictionary(x => x.Key, x => x.Value);
                if (expected.Count != actual.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var value) || value != x.Value))
                    throw new InvalidDataException("Immutable attempt hash verification failed.");
                var result = CampaignJson.Read<AttemptResult>(resultPath);
                if (result.Identity != start.Identity || result.Key != start.Key || result.Attempt != start.Attempt ||
                    result.Status is not ("success" or "failed") || (result.Status == "success" && result.Observation == null))
                    throw new InvalidDataException("Attempt result does not match reserved identity.");
                if (result.Status == "success") ValidateObservation(result.Key, result.Observation!);
                rows.Add(result);
            }
        }
        return rows;
    }

    /// <summary>
    /// Исполняет ключи матрицы, записывая каждую попытку в журнал и соблюдая политику повторов.
    /// Успешные ключи пропускаются (позволяет возобновление), а каждый неуспешный ключ получает
    /// до <paramref name="maxAttempts"/> попыток подряд с растущим номером.
    /// </summary>
    /// <param name="keys">Ключи матрицы в порядке исполнения.</param>
    /// <param name="execute">Обратный вызов попытки: аргументы — ключ, путь каталога попытки,
    /// номер попытки (1..N) и токен отмены; результат — валидируемое наблюдение.</param>
    /// <param name="maxRuns">Ограничение числа успешных ключей за вызов (<c>null</c> — без ограничения;
    /// используется <c>--runs</c> в pilot-прогонах).</param>
    /// <param name="cancellationToken">Токен отмены; пробрасывается как <see cref="OperationCanceledException"/>
    /// без записи failed-попытки.</param>
    /// <param name="maxAttempts">Максимум попыток на один ключ; при исчерпании выбрасывается
    /// <see cref="CampaignExhaustedException"/>.</param>
    /// <exception cref="CampaignExhaustedException">Ключ исчерпал <paramref name="maxAttempts"/> неуспешных
    /// попыток (в том числе обнаруженных в существующей истории при возобновлении).</exception>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="cancellationToken"/>.</exception>
    /// <exception cref="IOException">Каталог попытки уже существует (конфликт с историей).</exception>
    /// <exception cref="InvalidDataException">Наблюдение не прошло <see cref="ValidateObservation"/>.</exception>
    public async Task ExecuteAsync(IEnumerable<CampaignKey> keys, Func<CampaignKey, string, int, CancellationToken, Task<RunObservation>> execute,
        int? maxRuns = null, CancellationToken cancellationToken = default, int maxAttempts = CampaignDefaults.MaxAttempts)
    {
        var existing = ReadAttempts(root).ToList();
        foreach (var exhausted in existing.GroupBy(x => x.Key).Where(x => x.Count() >= maxAttempts && x.All(a => a.Status != "success")))
            throw new CampaignExhaustedException(exhausted.Key.Key, maxAttempts);
        var completed = 0;
        foreach (var key in keys)
        {
            if (existing.Any(x => x.Key == key && x.Status == "success")) continue;
            if (maxRuns.HasValue && completed >= maxRuns.Value) break;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var number = existing.Count(x => x.Key == key) + 1;
                if (number > maxAttempts) throw new CampaignExhaustedException(key.Key, maxAttempts);
                var path = Path.Combine(root, "attempts", key.Key, $"attempt-{number:00}");
                if (Directory.Exists(path)) throw new IOException("Attempt directory already exists.");
                Directory.CreateDirectory(path);
                var row = new AttemptResult(identity, key, number, "started", null, null);
                CampaignJson.WriteNew(Path.Combine(path, "started.json"), row);
                try
                {
                    var observation = await execute(key, path, number, cancellationToken);
                    ValidateObservation(key, observation);
                    row = row with { Status = "success", Observation = observation };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { row = row with { Status = "failed", Failure = error.GetType().Name + ": " + error.Message }; }
                CampaignJson.WriteNew(Path.Combine(path, "result.json"), row);
                CampaignJson.WriteNew(Path.Combine(path, "sha256.json"), CampaignJson.HashTree(path));
                existing.Add(row);
                if (row.Status == "success") { completed++; break; }
                if (number == maxAttempts) throw new CampaignExhaustedException(key.Key, maxAttempts);
            }
        }
    }

    /// <summary>
    /// Проверяет согласованность наблюдения с ключом: совпадение identity, конечная положительная
    /// длительность, баланс счётчиков публикаций/доставок, применимость латентности для QoS и RTT-базис.
    /// </summary>
    /// <exception cref="InvalidDataException">Любое из инвариантов наблюдения нарушено.</exception>
    internal static void ValidateObservation(CampaignKey key, RunObservation observation)
    {
        var m = observation.Measurement;
        if (key != observation.Key || !double.IsFinite(m.ActualMeasurementSeconds) || m.ActualMeasurementSeconds <= 0 ||
            observation.MeasurementEndedUtc <= observation.MeasurementStartedUtc || m.AttemptedPublishes <= 0 ||
            m.CompletedPublishes < 0 || m.FailedPublishes < 0 || m.AttemptedPublishes != m.CompletedPublishes + m.FailedPublishes ||
            m.CompletedIdsDelivered < 0 || m.CompletedIdsDelivered > m.CompletedPublishes ||
            m.UniqueDeliveries < 0 || m.DuplicateDeliveries < 0 || m.UnexpectedDeliveries < 0 ||
            m.DeliveredAfterFailedPublish < 0 || m.DeliveredAfterFailedPublish > m.FailedPublishes ||
            m.ErrorReasons.Values.Sum() != m.FailedPublishes || !double.IsFinite(observation.RttBaselineMs) || observation.RttBaselineMs < 0)
            throw new InvalidDataException("Inconsistent measurement counts, identity, observed duration or RTT.");
        if (key.Qos > 0 && m.CompletedPublishes == 0)
            throw new InvalidDataException("QoS1/2 successful runs require at least one completion latency sample.");
        if ((key.Qos == 0 && (m.PublishLatencyMs != null || m.LatencyStatus != "notApplicable")) ||
            (key.Qos > 0 && m.CompletedPublishes > 0 && (m.PublishLatencyMs == null || m.LatencyStatus != "observed")))
            throw new InvalidDataException("Latency applicability differs from QoS/completion counts.");
        m.PublishLatencyMs?.Validate();
    }

    /// <summary>Освобождает lock каталога кампании, разрешая последующий запуск (в том числе возобновление).</summary>
    public void Dispose() => lease.Dispose();
}
