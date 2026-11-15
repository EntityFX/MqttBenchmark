# Метрики реального брокера и артефакты v3

Единицей raw является успешная попытка для broker × payload bytes × QoS × publishers × repeat. N — число publishers; один subscriber создаётся дополнительно. Метрики измеряются на стороне генератора; успешная публикация и доставка подписчику учитываются раздельно.

## Наблюдаемые величины

Обозначим A — attemptedPublishes, C — completedPublishes, F — failedPublishes, D — completedIdsDelivered, T — actualMeasurementSeconds. Источник формул: [MeasurementSummary](../src/EntityFX.MqttBenchmark.Campaign/Workload/MeasurementSummary.cs).

| Величина | Формула/источник | Единица | Применение в MqttY |
|---|---|---|---|
| attemptedRps | A/T | публикаций/с | observed offered load; исходная capacity при rateRejectionRate=0 |
| completedRps | C/T | публикаций/с | targetCompletedRps, reference для fidelity |
| publishFailureRate | F/A; при A=0 — 0 | доля 0..1 | условный отказ после rate gate |
| deliveryLossRate | 1−D/C; при C=0 — 0 | доля 0..1 | условная потеря после успешной публикации |
| publishLatencyMs | от PublishAsync до завершения PUBACK/PUBCOMP, nearest-rank knots | мс | observed latency; после RTT-поправки processing latency |
| rttBaselineMs | медиана QoS0 MQTT echo probes в одном запуске | мс | поправка latency; среднее RTT повторов в observations |
| duplicateDeliveries | повторные receipts message ID | сообщения | диагностика, отдельного параметра профиля нет |
| unexpectedDeliveries | receipt без ожидаемого ID | сообщения | диагностика |
| deliveredAfterFailedPublish | receipt ID публикации с конечным failed outcome | сообщения | диагностика неоднозначного завершения |
| CPU/RSS/disk/NIC | worker и load-generator reports | %, байты, счётчики | валидация стенда, автоматически в calibration не импортируются |

QoS 0 latency = null/notApplicable: успешный возврат публикации не является MQTT ACK. QoS 1/2 p99 — задержка завершения публикации, а не end-to-end latency подписчика. Когда C=0, нулевая условная loss — соглашение отчёта, не доказательство отсутствия потерь. QoS1/2 без completed publishes не дают пригодной latency evidence.

T измеряется монотонно, включает завершение выданных in-flight publish операций после номинального cutoff. Поэтому делить count на measurementSeconds из config вместо T неправильно. Warmup имеет отдельные topic/ID и не участвует в итогах.

Payload содержит детерминированный 16-byte message ID, зависящий от campaign/attempt/publisher/sequence. Ledger считает уникальные завершённые публикации и доставки их ID; duplicate/unexpected receipts не делают loss отрицательной. После завершения drain ledger замораживается. Причины drain: все завершённые ID получены, quiet period либо timeout. Потери характеризуют выбранную установку и окно наблюдения; причину высокой loss нельзя автоматически приписать брокеру.

## Телеметрия и ресурсы

| Источник | Scope | Что можно утверждать |
|---|---|---|
| Native process CPU | process-pinned | CPU конкретного listener PID, нормированная на число выбранных ядер |
| Native RSS | broker-process | working set/RSS процесса, не вся память хоста |
| Native disk I/O | broker-process | Linux /proc/PID/io; в Windows может отсутствовать |
| Network / connections | host-shared | общие counters хоста и подключения порта, не изолированный сетевой трафик брокера |
| Load-generator CPU | whole system | нагрузка всей машины контроллера, а не только CLI |
| Load-generator NIC | routed interface | RX+TX по выбранному физическому NIC; unrelated traffic также попадает в counters |

Заявленный memoryLimit=4 GiB не равен фактическому enforcement. Проверяйте `memoryLimitEnforcement`, affinity и effective configs в run-provenance/versions. При external lifecycle настройки процесса принадлежат оператору. Недостающий counter не заполняется вымышленным нулём.

На loopback нет физической link capacity: network threshold должен быть null. Для удалённого host guard выбирает маршрут к IPv4 endpoint, требует физический NIC, стабильную скорость и непрерывные интервалы. В Linux sysfs speed выражена в Mbit/s и переводится в bit/s множителем 1 000 000. По умолчанию CPU gate — 70%, network gate — 80% (Aedes informational), maximum guard interval — 1.25 с; конкретный config может изменить их. RX+TX сравниваются совместно. Изменение порогов меняет методику и должно отражаться в сохранённых входах.

## Что сохранять и переносить

```text
campaign/
  campaign.json
  attempts/<broker.m.q.p.r>/attempt-NN/
    started.json
    publishes.ndjson
    deliveries.ndjson
    protocol.json
    load-generator-*.json / *.ndjson
    stand/
      stand.selected.json
      run-provenance.json
      clock-alignment.json
      telemetry.ndjson
      telemetry-ready.json
      workload-end-fence.json
      sampler-ended.json
      telemetry-manifest.json
      versions.json
      config-hashes.json
      logs/
    result.json
    sha256.json
aggregate/
  broker-observations.v3.json
```

Capture durations включают фазовый envelope и reserve. Сэмплы обязаны покрывать workload: ни ready в конце захвата, ни просто одинаковые UTC timestamps этого не доказывают. Не редактируйте sealed attempts; hashes проверяются при resume/aggregate. Hash provenance идентифицирует входы, но не удостоверяет их независимой подписью.

Для пересылки Linux может выполнить `tar -czf campaign.tar.gz <campaign-directory>` и `sha256sum campaign.tar.gz`; Windows — `tar.exe -czf campaign.tar.gz <campaign-directory>` и `Get-FileHash campaign.tar.gz -Algorithm SHA256`. Сначала прекратите запись в серию и сохраните весь campaign, config, stand, aggregate и версии кода. На получателе сравните SHA-256 архива и file seals. SSH private keys и пароли не включаются в пакет результатов.

## Агрегирование и достоверность

Стандартная матрица даёт 96 точек, каждая имеет три повтора. Rate statistics содержат mean, sample standardDeviation, Student-t CI95 с df=2, min/max/count. Наблюдаемые latency knots агрегируются по повторам вместе со статистиками каждого knot; это не p99 объединённой выборки всех сообщений. rttBaseline — среднее трёх per-run median RTT.

CI95 — статистический интервал, не физический диапазон параметра; границы могут выйти за 0..1. Три повтора ограничивают статистическую мощность. Разные campaign identities, source/config/stand hashes, CPU/deployment modes не смешиваются. Полнота observations ещё не означает приемлемую точность модели. Как преобразуются значения и как проверяется fidelity — см. документацию MqttY.

---

## Индекс документации MqttBenchmark (из docs/README.md)

MqttBenchmark измеряет реальный MQTT-брокер и сохраняет наблюдения для калибровки MqttY. Основной маршрут использует кампании v3 и native-процессы без Docker. Поддержаны локальное управление и SSH worker, автоматический и ручной запуск. Windows запускает Aedes/Mosquitto/Classic непосредственно; полный набор с EMQX — внутри Ubuntu WSL2. Проверка всех четырёх native-брокеров выполнена на текущей ALT Linux; Windows требует проверки на целевой машине.

| Задача | Руководство |
|---|---|
| Установить каждый брокер и проверить MQTT | [Быстрый старт](quick-start.md) |
| Подготовить Ubuntu, ALT или Windows | [Подготовка машины](machine-preparation.md) |
| Запустить пилот и локальную кампанию | [Локальный стенд](local-stand.md) |
| Выбрать 1, 2 или 3+ машины; native/container | [Варианты размещения](#размещение-1-2-и-3-машины) |
| Разделить контроллер и хост брокера | [Стенд на двух машинах](two-machine-stand.md) |
| Понять управление и сбор артефактов | [Архитектура стенда](architecture.md) |
| Настроить матрицу, retry и resume | [Контракт кампании v3](#контракт-кампании-v3) |
| Интерпретировать измерения | [Метрики](metrics.md) |
| Перенести опубликованную CLI | [Переносимое развёртывание](../DEPLOYMENT.md) |
| Применить наблюдения в симуляторе | Калибровка MqttY |

Полный исследовательский маршрут и сохранённые результаты: [общий индекс](../../diss/doc/README.md). Пути между репозиториями предполагают соседние каталоги `MqttBenchmark`, `mqtty`, `diss`, как в mqtty-meta.

## Статусы проверок

«Проверено» означает выполненную проверку с указанным окружением. «Подтверждено источником» — описанную возможность поставщика, но не проведённый здесь запуск. «Не проверено» — необходимую локальную валидацию. «Не поддерживается» — отсутствующий контракт текущего ПО или платформы. Итоговые проверки документационных примеров перечислены в [отчёте](../../diss/doc/validation.md).

В коде остаётся совместимость со старыми профилями, но этот комплект не предлагает обходного запуска через v2. Сбор 72 ключей одного брокера и получение observations ещё не дают полный набор, который принимает калибратор MqttY v3.

---

## Контракт кампании v3 (из campaign-v3.md)

Практические команды находятся в [локальном запуске](local-stand.md) и [двухмашинном запуске](two-machine-stand.md). Этот документ задаёт единую методику для обоих маршрутов и ОС.

### Матрица и входы

Стандарт: Aedes/Mosquitto/ActiveMQ/EMQX × payload 16/256 bytes × QoS 0/1/2 × publishers 1/16/64/128 × 3 repeats = **288 успешных ключей**, **96 агрегированных точек**. ActiveMQ означает Classic. Каждый запуск создаёт N publishers и одного subscriber. Seed 20260909 определяет порядок SHA-256 от seed/key; брокеры группируются для последовательного исполнения.

Campaign JSON (`schemaVersion: 3`) описывает нагрузку и gates; stand JSON (`schemaVersion: "broker-stand.v1"`) описывает endpoints, executable/launcher и ресурсы. `NewNativeStand.ps1` создаёт native-входы и задаёт `customImageBrokers: []`. Исходный `config/broker-stand.v1.json` — контейнерный инвентарь, его не используют в маршруте без Docker. Поля dockerContext/serviceName/containerName остаются в native schema как совместимые имена; Docker-процесс не вызывается.

| Параметр | Стандарт |
|---|---|
| warmup / measurement / cooldown | 5 / 30 / 5 секунд |
| drainTimeout / quietPeriod | 30 / 2 секунды |
| maxAttempts | 3 на ключ |
| preflightRttProbes / preflightSysWaitSeconds | 20 / 10 секунд ожидания SYS |
| transportTimeoutSeconds | 10 |
| telemetryCaptureReserveSeconds | 20 |
| telemetryReadinessTimeoutSeconds | 60 |
| CPU gate / maximum guard interval | 70% / 1.25 секунды |
| Network gate | 80% для удалённого физического NIC; Aedes informational в текущей методике; loopback всегда null |

Источники значений: [CampaignDefaults](../src/EntityFX.MqttBenchmark.Campaign/CampaignDefaults.cs), [CampaignDefinition](../src/EntityFX.MqttBenchmark.Campaign/CampaignDefinition.cs). Генератор записывает network policy явно. Изменение политики — новая campaign identity; старый Aedes gate 85% относится только к [исторической серии](../../diss/doc/results-and-evidence.md).

CLI позволяет другие размеры матрицы/длительности, aggregate требует покрытие именно заданного config. Калибратор MqttY строже: один брокер (72 ключа, 24 точки), короткий pilot или иное число repeats не принимаются как стандартная калибровка. Минимальное время 288×40 секунд фаз — 3.2 часа; сюда не входят preflight, запуск каждого процесса, reserve/capture, drain и retries.

### Идентичность и возобновление

Запуск требует чистых tracked файлов MqttBenchmark; `--benchmark-repo` указывает репозиторий исходников, не просто каталог published bin. Campaign manifest фиксирует ID, точные config/stand SHA-256, deployment/CPU mode и commit. Парсинг и хэш используют один byte snapshot. Stand snapshot проверяется до действий контроллера; его изменённые bytes не могут незаметно войти в новую попытку.

`matrix --resume` с теми же `--config`, `--stand`, `--campaign` проверяет seals и observations, пропускает только валидные успехи. Незавершённая unsealed попытка остаётся в истории и расходует retry budget. Удалять её ради повторного счётчика нельзя. Третья неудача останавливает кампанию, последующий resume не снимает этот предел. CLI `--max-attempts` меняет budget явно. Изменение tracked исходников, config или stand запрещает продолжение; новая машина/версии/конфигурации требуют новой кампании даже там, где CLI не может автоматически обнаружить их изменение.

Native identity проверяет фактический listener PID, стабильную process start identity, resolved executable SHA-256 и launcher SHA-256. Конфигурационные файлы хэшируются при старте и проверяются во время действий этого run. Это не полная фиксация всех библиотек, env или машины: оператор дополнительно сохраняет зависимости и состояние окружения. Не обновляйте бинарные файлы, конфигурации или node_modules в ходе серии. External процесс принадлежит оператору; его state/reset policy также фиксируется.

### Условия успешной попытки

1. Validate → start/attach → MQTT health CONNECT/CONNACK → clock. Native local имеет один host clock; SSH проверяет remote clock с консервативной uncertainty. Default stand bound: abs(offset)+uncertainty <=3000 ms, допустимый wall-clock step <=100 ms. Это не сертификация синхронизации до миллисекунды.
2. Protocol preflight: отдельные CONNECT/SUBSCRIBE/PUBLISH/доставка для QoS 0/1/2, RTT echo. `$SYS/broker/version` может отсутствовать: null/unavailable с сохранённой диагностикой, версия executable записывается отдельно.
3. Capture сохраняет первый sample и атомарно публикует telemetry-ready **до workload**. Конец sampler публикуется отдельно. Workload-end-fence использует monotonic timestamp того же listener/runtime; последний sample должен быть строго позже fence.
4. Load-generator guard на Windows/Linux получает baseline и интервалы, покрывающие actual measurement, включая in-flight completion. Loopback использует whole-system CPU и informational network; remote IPv4 требует физический routed NIC с положительной скоростью. Windows проверяет adapter metadata, Linux — procfs/sysfs. Виртуальный NIC контроллера не проходит remote guard.
5. Каждый пересекающий measurement интервал оценивается целиком. CPU и RX+TX/link должны быть в заданных пределах; counter reset, route/link change, разрывы, overlapping reads и неполное покрытие отклоняют попытку. Нет bypass guard. One-second averages не видят все субсекундные пики.
6. Result и SHA-256 seal сохраняются после завершения writers/cleanup. QoS1/2 без completed publishes не имеют требуемой latency и не считаются успешными. Отказы/partial bytes сохраняются.

Телеметрия брокера и guard генератора — разные источники. Формулы нагрузки и состава артефактов: [метрики](#метрики-реального-брокера-и-артефактов-v3). Aggregate проверяет полное уникальное покрытие, identity/modes и seals; не смешивает разные кампании. Команда обязательно содержит `--stand`, даже если aggregate не запускает брокер.

### Ошибки и остановка

| Ошибка | Действие |
|---|---|
| Занят MQTT-порт | Проверить listener на broker host; остановить принадлежащий эксперименту процесс либо выбрать новый port/inventory до новой кампании |
| Неверный executable/launcher SHA-256 | Проверить путь/дистрибутив; пересоздать входы для новой серии, не менять pin существующей кампании |
| Изменены входы при resume | Восстановить точные исходные bytes/commit либо начать отдельную кампанию |
| Неполная матрица | Завершить недостающие ключи в той же допустимой identity; не достраивать observations выдуманными значениями |
| Output существует | Выбрать новый output; для raw campaign использовать только supported resume |
| Guard/clock/readiness failure | Сохранить failed attempt, устранить причину; не объявлять протокольную доступность успешным измерением |

Остановка CLI — Ctrl+C. Managed cleanup прекращает только проверенный процесс; external оставляет его оператору. При потерянном SSH применяются правила lease recovery [двухмашинного руководства](two-machine-stand.md). Default v3 retry/resume и sealed results одинаковы при запуске через RunNativeCampaign.ps1 и отдельные CLI-команды.

---

## Размещение: 1, 2 и 3+ машины (из deployment-layouts.md)

Место исполнения и runtime — независимые настройки. Контроллер — CLI, MQTT publishers/subscriber и хранилище raw; broker host — процессы или Linux-контейнеры. Каждая физическая машина может иметь Linux или Windows. EMQX на Windows требует Linux окружения WSL2 либо Linux-контейнеров; native Windows EMQX не поддерживается поставщиком.

| Размещение | Роли | deploymentMode | Native | Контейнеры |
|---|---|---|---|---|
| 1 машина | Контроллер и брокеры на одном хосте | singleHostSequential | [Локальный запуск](local-stand.md) | Local Docker context, Linux containers |
| 2 машины | Контроллер + общий хост брокеров | singleHostSequential | [SSH worker](two-machine-stand.md), root nativeHost | Remote Docker context общего хоста |
| 3+ машины | Контроллер + несколько хостов брокеров | distributedHosts | nativeHost каждого broker | dockerContext каждого broker |

Количество брокеров не равно количеству машин. Четыре брокера могут по очереди работать на одном хосте либо быть распределены по нескольким. **Matrix v3 выполняет измерения последовательно**, даже в distributedHosts: это размещение экспериментов, не автоматический эксперимент связанной сети реальных брокеров. Сеть симулируемых брокеров создаётся в MqttY.

### Native на 3+ машинах: через скрипты

Подготовьте контроллер и каждый broker host по [подготовке ОС](machine-preparation.md), [установке брокеров](quick-start.md) и [SSH](two-machine-stand.md). SSH/data endpoints должны быть доступны контроллеру. Для каждого брокера на **его машине** создайте one-broker spec, затем stand/campaign с NewNativeStand.ps1 и NativeHostPath. Все источники используют одинаковый CPU mode; pins относятся к бинарным файлам именно этого хоста. MQTT-порт может быть 1883 на разных IP; endpoints обязаны быть уникальными.

На каждом broker host, PowerShell 7 из MqttBenchmark:

```powershell
./scripts/NewNativeStand.ps1 -BrokerSpecPath config/brokers.spec.local.json `
  -StandPath config/one-broker.local.json -CampaignPath config/one-campaign.local.json `
  -NativeHostPath config/native-host.local.json -CpuMode hostAllCores -Lifecycle managed
```

NativeHost JSON задаётся как в двухмашинном руководстве, пути SSH keys относятся к будущему контроллеру. Каждый source stand содержит один брокер. Перенесите файлы stand на контроллер через scp, под разными именами; campaign отдельных хостов не объединяют с результатами.

На контроллере из MqttBenchmark:

```powershell
./scripts/MergeNativeStands.ps1 `
  -InputStandPaths config/aedes-export.local.json,config/mosquitto-export.local.json,config/activemq-export.local.json,config/emqx-export.local.json `
  -StandPath config/distributed-stand.local.json -CampaignPath config/distributed-campaign.local.json
./scripts/RunNativeCampaign.ps1 -ConfigPath config/distributed-campaign.local.json `
  -StandPath config/distributed-stand.local.json -CampaignDirectory artifacts/distributed-pilot-001 -Pilot
```

Ожидаются merged distributedHosts config/stand и один successful pilot key после preflight всех выбранных брокеров. Root nativeHost не нужен; worker выбирается по active broker. Broker nativeHost имеет приоритет над root nativeHost. Разные Windows/Linux абсолютные пути не разрешаются на чужой машине: их проверяет соответствующий worker. Lease защищает выбранный workRoot; у каждого физического стенда используйте единый workRoot для всех контроллеров.

### Native на 3+ машинах: руками

Вместо MergeNativeStands.ps1 соберите новый JSON по stand.v1: deploymentMode=distributedHosts, brokers — записи из one-broker exports; каждой добавьте nativeHost своего хоста и уникальный dockerContext alias, например native-ssh-host-a. Alias нужен для lease и provenance; Docker им не запускается. Root dockerContext тоже остаётся обязательным schema field. Выставьте ровно один loaded=true, соответствующий activeBroker. Для нескольких brokers singleHostMqttPort — стандартные 1883/2883/3883/4883 (неиспользуемая здесь однохостовая карта); distributedMqttPort — фактический port данного endpoint. Для каждого runtime=native, executable/launcher hashes вычислены на broker host.

Campaign: schemaVersion3, deploymentMode=distributedHosts, тот же cpuMode, четыре стандартных broker names, customImageBrokers=[], полная матрица и явные guard thresholds. Ручные preflight/matrix/resume/aggregate — [локальный CLI runbook](local-stand.md) с этими новыми config/stand. Для ручных broker processes задайте lifecycle=external и предварительно запустите нужный брокер на соответствующем хосте. CLI stop оставляет external process оператору. Ctrl+C выполняет managed cleanup выбранного worker; потерянный SSH требует recovery lease.

### Контейнерные варианты

Основной quick start пакета — без Docker. Контейнерный runtime уже существует в v3: при отсутствии runtime entry считается container, а nativeHost не используется для такого active broker. Контейнеры этих брокеров — **Linux containers**, телеметрия использует Linux cgroup/procfs. Linux Engine поддерживает host networking; Windows Docker Desktop 4.34+ требует Linux backend и явно включённого host networking, либо Docker Engine внутри WSL2 с forwarding по двухмашинному руководству. Windows containers не поддерживаются этим контрактом. [Официальные границы Docker host network](https://docs.docker.com/engine/network/drivers/host/).

На контроллере требуется Docker CLI + Compose plugin; на broker host — доступный Linux Docker Engine. На Linux ставьте Engine по инструкции своей ОС; на Windows — Docker Desktop с указанным backend. Контроллер Windows может управлять Linux Engine, даже если его собственный daemon не используется. Для удалённого Engine через SSH учётная запись должна иметь доступ к его socket. После проверки SSH с ключом и known_hosts, из MqttBenchmark на контроллере:

```powershell
docker context create broker-host-a --docker 'host=ssh://researcher@192.0.2.20'
docker --context broker-host-a info
docker compose version
```

Ожидается удалённый Linux Engine; `192.0.2.20` заменить реальным endpoint. Для других hosts создайте отдельные contexts. [Официальный SSH transport Docker](https://docs.docker.com/engine/security/protect-access/). На Windows broker host SSH endpoint для этого способа — Linux WSL с Docker Engine; вариант прямого remote Desktop требует отдельной настройки daemon доступа.

Скопируйте config/broker-stand.v1.json в config/container-stand.local.json. Для одной/двух машин оставьте singleHostSequential и задайте root dockerContext нужного Engine; mqttUri — loopback либо data IP broker host с 1883/2883/3883/4883. Для 3+ машин задайте distributedHosts, у каждой broker записи dockerContext своего Engine и mqttUri его data IP:1883. controlInterface должен существовать в Linux broker environment для management bind; удобнее 127.0.0.1 с managementUri loopback и отдельным SSH доступом к UI. Согласуйте campaign deploymentMode/cpuMode. Не заменяйте проверенные digest выдуманными; для custom Aedes/Classic контроллер сохраняет реальные build inputs/image ID.

Через CLI выполните те же preflight/matrix/resume/aggregate из local runbook. Для контейнерного campaign сохраните customImageBrokers=[Aedes,ActiveMQ]. Native-generated config с пустым customImageBrokers нельзя механически применять к custom контейнерам без этой настройки. Script RunNativeCampaign.ps1 делегирует CLI и работает с таким inventory, но не устанавливает Docker. Артефакты container-cgroup CPU и native process-pinned CPU имеют разные scopes; результаты разных runtimes сохраняйте отдельными кампаниями.

Для ручного управления **контроллером контейнеров** используйте BrokerStand.ps1 -Action pull/validate/start/health/clock/capture/stop/reset с конкретными config/output, как в manual controller разделе local runbook. Это запуск через существующие controller actions с сохранённой provenance; произвольный docker run без них не создаёт требуемую v3 stand evidence. Остановка — CLI Ctrl+C либо controller stop; reset удаляет только заданный контейнер. Native external lifecycle не переносится на контейнеры.

### Статус и сравнимость

Native local четыре брокера проверены на текущей ALT; SSH и выбор per-broker host проверяются функциональными tests. Запуск нескольких отдельных физических машин, Windows/WSL и новых container layouts здесь не проведён; сохранённые исторические контейнерные серии описаны в [результатах](../../diss/doc/results-and-evidence.md). Возможность ОС/vendor и фактическая acceptance конкретного стенда — разные статусы.

Смена числа машин, runtime, версии, NAT, CPU mode или сетевого пути требует новой серии. Нельзя объединять старые container и новые native points ради формальной полноты calibration. Полная матрица и fidelity подтверждаются отдельно от проверки запуска.

---

## Последовательная серия с отдельными отчётами (из two-machine-campaign.md)

Контроллер Windows выполняет нагрузку и MqttY, Linux запускает native-брокеры. `scripts/campaign/prepare.py` создаёт отдельные pinned входы каждого брокера, `scripts/campaign/run.py` выполняет Aedes → Mosquitto → ActiveMQ Classic → EMQX, затем смешанную сеть. JSON серии хранит точные пути, commits фиксируются CLI.

Перед подготовкой нужны .NET 6, PowerShell 7, отдельный SSH-ключ и установленное окружение брокеров. На Linux инструменты лежат на evidence disk; проектные скрипты не устанавливают и не обновляют брокеры в ходе измерений. Инвентарь задаёт `linuxLaunchMode=systemdUser`: transient user service сохраняет брокер после закрытия SSH-сеанса, применяет CPU affinity до создания потоков и MemoryMax. Требуются пользовательский systemd/D-Bus и Python3 с dbus-python. Переменные окружения передаются в память manager через D-Bus, без env-файлов или значений в argv. Фактические ограничения проверяются в procfs/cgroup. `scripts/campaign/requirements.txt` задаёт зависимости генератора документов.

```powershell
python scripts/campaign/prepare.py --series artifacts/two-machine-<new-id> --identity-file C:/Users/<user>/.ssh/mqtty_two_machine
python scripts/campaign/run.py --series artifacts/two-machine-<new-id>
python scripts/campaign/run.py --series artifacts/two-machine-<same-id> --resume
python scripts/campaign/run.py --series artifacts/two-machine-<same-id> --verify-only
```

`prepare.py --cpu-threshold-percent 99` фиксирует явно выбранный пользователем порог CPU вместо стандартных70%. Это новая campaign identity; прежние попытки не смешиваются с ней. Насыщение генератора не объявляется пределом брокера. Raw scratch создаётся в `$USERPROFILE/.mqtty-scratch/<series>/raw`, вне workspace IDE, чтобы события записи подробных журналов не нагружали file watcher редактора. В каталоге reports остаются документы, а на hd250 — полные evidence ZIP.

### Резервный совместный запуск на Linux

Если Windows-контроллер не проходит guard, пользователь разрешил отдельную Linux-серию. `prepare-linux-controller.py --series <hd250/new-series> --repository <MqttBenchmark> --mqtty-repository <mqtty> --fallback-evidence <previous-series>` создаёт независимые frozen входы: MQTT через127.0.0.1, CPU guard99%, network guard informational. Pilot использует полное окно5/30/5с, чтобы короткая нагрузка не скрывала перегрузку.

Контроллер запускается в owned systemd user service с CPU1–11,13–23 и MemoryMax8GiB. Брокеры используют CPU0/MemoryMax4GiB; SMT sibling12 исключён из контроллера. Фоновые процессы не перемещаются, эксклюзивность не заявляется. Systemd native launch проверяет доступную affinity менеджера, затем фактическую affinity всех потоков брокера. CPU guard относится ко всему Linux-хосту. `MQB_GUARD_SAMPLER_CLI` запускает счётчики в отдельном лёгком .NET-процессе: выделенный поток внутри генератора также приостанавливается при GC генератора. Sampler использует тот же OS monotonic clock; frequency, PID, CLI hash и raw интервалы сохраняются. Оценка CPU99%, max interval1.25с, reset/overlap и bracket остаётся прежней. Потеря sampler приводит к отказу, а не к пропуску проверки.

Controller bundle, raw, evidence, TMPDIR, Python venv/cache и simulator bundles лежат на hd250 в отдельных каталогах. System disk проверяется отдельно от raw. `matrix --archive-mount /run/media/entityfx/hd250 --archive-device /dev/sdc1` проверяет TARGET/SOURCE/ext4 перед архивированием, перед каждой попыткой и непосредственно перед очисткой. При повреждении/потере mount raw сохраняется; resume восстанавливает receipt уже опубликованного проверенного ZIP и завершает безопасную очистку после восстановления storage.

Для Linux loopback нельзя вывести ёмкость физического линка из натурных данных. 125000000 bytes/s в модели сети — явно указанное допущение Ethernet1Gbps; RTT берётся из новой Linux-калибровки, queueCapacity100000 — допущение. Профили и измерения двух машин не смешиваются с новой серией. `watch-linux.py --source <SMB/new-series/controller> --destination <local-artifacts>` копирует компактные документы на Windows каждые30с; native runner от SSH не зависит.

Подготовка storage выполняется отдельно: `configure-storage.py` создаёт отдельную учётную запись и ограниченный controller IP SMB share. Пароль читается из stdin; существующие учётные записи и службы сохраняются. На контроллере credential должен храниться в Windows Credential Manager, а не в JSON серии.

### Порядок и статистика

На каждого брокера: pilot, 72 calibration measurements, immutable profile, 72 calibration fidelity simulations, 72 independent real measurements, 72 holdout simulations, 72 homogeneous relay simulations. После четырёх брокеров: 72 mixed simulations. Payload 16/256, QoS0/1/2, publishers1/16/64/128, repeats3; native seeds20260909/20260910, simulation seeds42/43/44. Pilot — три QoS с 128 publishers; его результаты не используются для калибровки.

Натурные длительности5/30/5с, drain30с; simulation tick0.1мс, warmup5с, measurement30с, drain30с. Transport фиксируется из calibration до holdout: one-way transfer ticks округляются из половины среднего MQTT echo RTT. Это приближение, включающее echo overhead брокера; оно не является точной идентификацией сетевого пути. Capacity125000000 bytes/s соответствует проверенному линку1Gbps; queueCapacity100000 — допущение.

Однородный relay offeredRps=4×attemptedRps; mixed=4×minimum attemptedRps четырёх соответствующих точек. Это внешняя суммарная нагрузка, внутренняя пересылка дополнительна. Пороги fidelity применяются только к одиночному BrokerFidelity; для relay reference остаётся диагностикой.

### Ограничение дискового объёма и доказательства

`matrix --archive-root <external-path>` переносит каждую sealed attempt в ZIP после завершения workload, telemetry, guard и cleanup. Хэш архива, исходный seal и все entries проверяются до удаления подробных локальных файлов. На Windows остаются started/result/seal/archive receipt. `resume` и `aggregate` проверяют архивы, сохраняют историю ошибок и исходный retry budget. SHA-256 вычисляется потоком, без загрузки больших журналов целиком в RAM.

В серии Windows требуется≥10GiB перед командой; watchdog прерывает при<6GiB. CLI не начинает следующую attempt при<8GiB. На evidence disk требуется≥20GiB, watchdog прерывает при<12GiB. Проверяется точный mount TARGET/SOURCE/FSTYPE. Guard читает counters на выделенном потоке AboveNormal и ждёт монотонный deadline через cancellable kernel wait; sampling не зависит от thread pool генератора. Разрывы>1.25с, resets и overlaps по-прежнему отклоняют attempt. На системных разделах не создаются полные копии verification; восстановление и повторный aggregate идут на hd250. Смена диска, обрыв связи, checksum failure или нехватка места останавливают серию и сохраняют доказательства.

Повторная агрегация восстановленных raw обязана совпасть по scientific points и campaign identity. InputSha256 отличается для compact/полного представления; оба observations и их хэши сохраняются в archive-verification.json.

Пять Markdown/HTML документов обновляются по этапам. HTML включает растровые графики внутрь документа; PNG/SVG/CSV/JSON также доступны отдельно. CPU/RSS отчёта относятся к полному capture envelope, что явно подписано. Receiver counters симуляции исключают warmup baseline, но сообщения warmup in-flight могут пересечь границу измерения. Недостающие counters не заменяются нулями.

При выходе experiment3 сохраняется провал fidelity и выполняется остальная матрица. Выходы1/2 — техническая остановка, перед resume требуется устранить причину. Не изменять код, профили или входы ради прохождения критериев.
