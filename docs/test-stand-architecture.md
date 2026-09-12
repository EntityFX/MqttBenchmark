# Тестовый стенд MqttBenchmark — архитектура, запуск, метрики, пререквизиты

Документ описывает измерительный стенд, на котором выполняется кампания замеров из 288 точек (4 брокера × 2 размера полезной нагрузки × 3 уровня QoS × 4 числа издателей × 3 повтора). Он дополняет `docs/campaign-v3.md` (схема кампании v3 и формулы агрегации). Фокус здесь — **как устроен стенд, как взаимодействуют компоненты, как он запускается, какие метрики собираются и какие нужны пререквизиты**.

Ключевые исходники, на которые опирается документ:

| Компонент | Путь |
|---|---|
| PowerShell-контроллер жизненного цикла брокеров | `MqttBenchmark/scripts/BrokerStand.ps1` |
| CLI кампании (preflight/matrix/aggregate) | `MqttBenchmark/src/EntityFX.MqttBenchmark.Cli/Program.cs` |
| Диспетчер команд кампании | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/CampaignCommands.cs` |
| Оркестрация одного измерения + захват телеметрии | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/StandSession.cs` |
| Протокольный preflight + загрузочный раннер | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/MqttCampaignRunner.cs` |
| Учёт доставок/ошибок + drain-политика | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/DeliveryLedger.cs` |
| Гейты CPU/сети на генераторе нагрузки | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/LoadGeneratorGuard.cs` |
| Windows-счётчики (CPU/NIC/hardware evidence) | `MqttBenchmark/src/EntityFX.MqttBenchmark/Campaign/WindowsLoadGeneratorCounters.cs` |
| Сборщик телеметрии в контейнере (cgroup v2) | `MqttBenchmark/docker/brokers/telemetry.sh` |
| Compose-описание 4 брокеров | `MqttBenchmark/docker/brokers/compose.yml` |
| Инвентарь стенда (реестр брокеров) | `MqttBenchmark/config/broker-stand.v1.json` |
| Настройка кампании v3 | `MqttBenchmark/config/benchmark-campaign.v3.json` |
| Документация стенда | `MqttBenchmark/docker/brokers/README.md` |

---

## 1. Назначение и место в системе

Цель стенда — получить **воспроизводимые и провенансируемые** метрики нагрузки на реальных MQTT-брокерах (Aedes, Mosquitto, ActiveMQ Classic, EMQX), чтобы по ним строить профили/калибровку для `mqtty`. Конвейер:

```
Стенд (broker host, Linux/Docker)
   └─> raw-артефакты (попытка за попыткой)
        └─> aggregate  =>  broker-observations.v3.json  (96 точек)
             └─> mqtty calibrate  =>  broker-calibration.v3.json
```

Принципы, зашитые в код:

- **Воспроизводимость**: каждый запуск фиксирует SHA-256 всех входных (инвентарь, compose, конфиг, сборочные входы), git-ревизии и хэш дерева артефактов. Запечатанную попытку невозможно «переписать».
- **Фиксация ресурсов**: единый закреплённый CPU (`singleCorePinned`, cpuset `0`), 4 GiB RAM, host-сеть. Результаты агрегируемы только внутри одного режима развёртывания и одного CPU-режима.
- **Fail-closed**: сбой захвата телеметрии, несоответствие хэшей, провал гейтов CPU/сети — это провал попытки, а не «оценка по среднему».
- **Безопасность**: в исходниках/артефактах/командах не хранятся пароли/ключи; реестровые образы только по проверенному SHA-256 digest.

---

## 2. Компоненты стенда

Стенд — это **две физические машины**, связанные SSH/TCP:

1. **Контроллер / генератор нагрузки** (Windows) — запускает CLI, шлёт MQTT-нагрузку, меряет CPU/сеть генератора, оркестрирует lifecycle брокеров через PowerShell.
2. **Хост брокеров** (Linux) — Docker Engine + 4 брокера в контейнерах на host-сети; внутри контейнера работает sampler `telemetry.sh`.

### 2.1 Хост брокеров (Linux)

По данным handoff (`CONTINUATION-HANDOFF.md`):

- Адрес: `10.10.157.111`, SSH-пользователь `gdc`.
- ОС: ALT Workstation 11.1 / p11, Linux x86-64.
- Железо: Intel i5-6400T, 4 ядра / 4 CPU, 8 GB RAM, Samsung SSD 850 EVO, витая пара.
- Docker Engine 29.2.1 + Compose 5.0.2, `gdc` в группе `docker`.
- На хосте также работает «bare-metal» Mosquitto 2.0.15 на TCP 9883 — он **не** управляемый брокер стенда (только пилот), его не трогают.

### 2.2 Брокеры (4 образа)

| Брокер | Образ | Тип | MQTT (single-host) | Management | Провенанс |
|---|---|---|---|---|---|
| Aedes | `local/mqttbenchmark-aedes:1.1.2-node20-alpine-build-inputs-sha256` | кастом (сборка, Node 20) | `1883` | — | локальный image ID + хэши сборочных входов |
| Mosquitto | `eclipse-mosquitto:2.1.2-alpine` | реестр | `2883` | — | `sha256:6f8d8a947c506f8a2290ec65cd4bd2bc7cb4d43fb5f6271f861cb013e2ef9797` |
| ActiveMQ | `local/mqttbenchmark-activemq:6.3.2-build-inputs` | кастом (сборка, Java) | `3883` | `http://…:8161` | локальный image ID + хэши сборочных входов |
| EMQX | `emqx/emqx:6.3.0` | реестр | `4883` | `http://…:18083` | `sha256:3b6d9c0c419930074b7e0e6cba07baceea584b2094d22f16ff02f6c075c3d5b8` |

Свойства всех контейнеров (из `compose.yml` и инвентаря):

- `network_mode: host` — весь MQTT/управляющий трафик идёт напрямую через host-сеть, без NAT/bridge.
- `mem_limit: 4g` (в инвентаре `memoryLimit: 4294967296`).
- `cpuset: "0"` — закрепление на **один** физический ядро (режим `singleCorePinned`).
- Persistence отключён (данные/логи — на `tmpfs`); лог `local`, `max-size 1m`, `max-file 3`.
- `mqttbenchmark.broker=true` — служебный label для изоляции контейнеров.

В режиме `singleHostSequential` запуск выбранного брокера сначала удаляет **все** другие контейнеры с этим label, затем поднимает ровно один активный. Это гарантирует эксклюзивность и одинаковые условия для всех 288 точек.

### 2.3 Контроллер (Windows)

- PowerShell (pwsh) для `BrokerStand.ps1`.
- .NET 6 (`dotnet run --project src/EntityFX.MqttBenchmark.Cli`).
- Docker CLI 29.x + Compose-плагин, настроенный контекст на хост брокеров.
- Стек генератора: `MQTTnet` (клиенты) + Windows-счётчики (`GetSystemTimes`, NIC counters, `Get-NetAdapter`).

### 2.4 Транспорт (SSH-контекст или TCP-relay)

- **Основной** — Docker context, указывающий на хост через SSH (в handoff: `mqtty-stand` → `ssh://gdc@10.10.157.111`, неинтерактивный вход по выделенному ключу).
- **Альтернативный (текущий отладочный)** — TCP-relay на порту `23762`, пробивающий Docker daemon к Unix-сокету с **коалесцингом** HTTP `101 UPGRADED` + первого кадра (требование Go docker CLI). Подробности и текущее состояние — в разделе 8.

> Инвентарь `config/broker-stand.v1.json` хранит `dockerContext` (в закоммиченной версии — `default`); на реальном стенде локальная версия (`config/*.local.json`) указывает на контекст хоста `10.10.157.111`.

---

## 3. Топология и взаимодействие

```
                    ┌────────────────────────────────────────────────────┐
   Windows          │  MqttBenchmark CLI (dotnet, .NET 6)                │
  контроллер        │   └─> BrokerStand.ps1  (lifecycle брокеров)        │
   + генератор      │   └─> LoadGeneratorGuard (CPU/сеть, гейты)         │
   нагрузки         │   └─> N × MQTT-издатели + 1 подписчик (MQTTnet)    │
                    └───────────────┬─────────────────────┬──────────────┘
                          управление/SSH            MQTT (host-сеть)
                        [контекст mqtty-stand]       1883/2883/3883/4883
                 ┌──────────────────────────┴───────────────────────────────┐
                 │  Linux 10.10.157.111 — Docker Engine + Compose           │
                 │  ┌──────────────────────────────────────────────────────┐│
                 │  │ контейнер брокера (network_mode: host, 1 CPU, 4 GiB) ││
                 │  │  Aedes | Mosquitto | ActiveMQ | EMQX                 ││
                 │  │  + sampler: telemetry.sh (cgroup v2, /proc)          ││
                 │  └──────────────────────────────────────────────────────┘│
                 └───────────────────────────────────────────────────────────┘
```

Каналов два, и они принципиально разделены:

1. **Управляющий** (Windows → Linux): Docker CLI поверх SSH-контекста (или TCP-relay). Идёт только «управление»: pull/start/health/exec/capture. Через него же запускается sampler `telemetry.sh` (`docker exec … sh -c …`).
2. **Рабочий** (Windows → брокер, host-сеть): «чистый» MQTT TCP на порту выбранного брокера. Через него идут сами издатели/подписчик. Скорость и качество именно этого канала — предмет измерения.

### 3.1 Последовательность одного ключа (key = broker·m·q·p·r)

Один «key» из 288 — это один полный цикл. Код (`StandSession.StartAsync` → `RunWithTelemetryAsync` → `CampaignCommands.ExecuteAsync`):

1. **Замок контекста** (`LeaseContext`): файл-лейс на `dockerContext` в `%TEMP%`. Защита от параллельных кампаний, работающих на одном хосте.
2. **Фиксация входных**: читаются байты инвентаря и сверяются с `standSha256` кампании; копируются в `inventory-check/stand.source.json`.
3. **pull** (только для Aedes/ActiveMQ — кастомные образы): сборка/проверка локального образа и запись провенанса. Реестровые (Mosquitto/EMQX) берутся из локального кэша по digest, без обращения в реестр.
4. **validate**: проверка схемы, digests, cpuset, memory, эксклюзивного брокера, campaign-совместимости.
5. **start**: `BrokerStand.ps1` удаляет прочие контейнеры с label и поднимает активный брокер; сохраняются `run-provenance.json` (imageId, effectiveConfigs, хэши input).
6. **health**: ожидание MQTT-ready + протокольная проверка.
7. **clock**: выравнивание/запись часов (daemon clock).
8. **Захват телеметрии** (параллельно): `docker exec … telemetry.sh` на удалённой стороне, sampler пишет NDJSON по монотонным дедлайнам `/proc/uptime`.
9. **Ожидание `telemetry-ready.json`** (до 60 c) — sampler обязан «схватить» базовую точку до старта нагрузки.
10. **Измерение**: `measurementObserver.MeasurementStarted` → окна `warmup` (5 c) → `measurement` (30 c) → `MeasurementEnded`. Генератор публикует N издателей, подписчик считает доставки; `DeliveryLedger` ведёт учёт.
11. **Drain**: до `allCompletedDelivered` / `quietPeriod` (2 c) / `timeout` (30 c).
12. **Cooldown** (5 c) → `fence` (снова `cat /proc/uptime` после завершения нагрузки) → сверка, что последний sample телеметрии позже fence и все containerId совпадают.
13. **Seal**: `telemetry-manifest.json`, `preflight.json`, `sha256.json` (хэш дерева артефактов), `publishes.ndjson`, `deliveries.ndjson`, `load-generator-summary.json`, журнал попытки (`campaign.json`, `attempts.json`).

После этого `StandSession.DisposeAsync` останавливает контейнер (`BrokerStand.ps1 -Action stop`).

---

## 4. Запуск

Запуск возможен двумя способами: через **CLI кампании** (рекомендуется, запускает весь lifecycle сам) и **вручную через `BrokerStand.ps1`** (для отладки/одиночных действий).

### 4.1 Действия `BrokerStand.ps1`

Скрипт принимает `-Action` из набора (валидируемый `ValidateSet`):

| Action | Что делает |
|---|---|
| `validate` | Проверяет схему инвентаря, digests, cpuset/memory, эксклюзивного брокера, campaign-совместимость |
| `pull` | Для кастомных (Aedes/ActiveMQ) — сборка/проверка локального образа; для реестровых — `docker pull image@digest` |
| `start` | Удаляет прочие контейнеры с label, поднимает активный брокер, записывает `run-provenance.json` |
| `health` | Ожидание MQTT-ready + протокольная проверка |
| `clock` | Выравнивание/запись часов (daemon clock) |
| `fence` | `cat /proc/uptime` после нагрузки — монотонный fence завершения работы |
| `capture` | Захват телеметрии на `-CaptureSeconds` секунд (sampler) |
| `stop` | `docker stop` контейнера |
| `reset` | `docker rm -f` контейнера |

Каждое действие пишет `controller-<action>.json` (exit code, stdout/stderr) и требует, чтобы выходной каталог был уникальным на кампанию.

### 4.2 Команды CLI (рекомендуемый путь)

Все команды запускаются из корня репозитория `MqttBenchmark`:

```powershell
# 1) Preflight — проверка протокола (CONNECT/SUBSCRIBE/PUBLISH/DELIVERED для QoS 0/1/2),
#    20 RTT-зондов ($SYS/broker/version, захват среды и телеметрии).
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- preflight `
  --stand config/broker-stand.local.json `
  --config config/benchmark-campaign.v3.json `
  --output artifacts/preflight-001 `
  --broker Mosquitto          # необязательно; ограничение на одного брокера

# 2) Matrix — полный цикл по 288 ключам (или подмножеству).
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
  --stand config/broker-stand.local.json `
  --config config/benchmark-campaign.v3.json `
  --campaign campaigns/baseline-001

# 3) Matrix с resume — пропускает успешные ключи, разрешает до 2 повторных попыток.
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- matrix `
  --stand config/broker-stand.local.json `
  --config config/benchmark-campaign.v3.json `
  --campaign campaigns/baseline-001 --resume

# 4) Aggregate — агрегация raw-артефактов в broker-observations.v3.json (96 точек).
#    Требует полноты 288/288.
dotnet run --project src/EntityFX.MqttBenchmark.Cli -- aggregate `
  --config config/benchmark-campaign.v3.json `
  --raw campaigns/baseline-001 `
  --output artifacts/aggregate-001
```

Полезные опции (из `Program.cs::PrintHelp`):

- `--broker <name>` — сузить preflight/matrix на одного брокера.
- `--max-runs <n>` — ограничить количество новых успешных ключей (пилот).
- `--dry-run` — показать, сколько ключей выбрано, без запуска.
- `--resume` — продолжить кампанию, пропуская успешные ключи.
- `--docker-executable <path>` — явный путь к docker CLI (если не в PATH).
- `--benchmark-repo <dir>`, `--stand-script <path>` — явные пути к исходникам/скрипту.
- `--trusted-build-provenance <dir>` — доверенный каталог провенанса кастомных сборок.

### 4.3 Пилот (короткий цикл)

`config/campaign-pilot.local.json` — та же матрица v3, но с короткими окнами: `warmup 0.2 s`, `measurement 1 s`, `cooldown 0.1 s`, `drain 0.5 s`, `quiet 0.2 s`. Пилот нельзя агрегировать (не даёт 288/288), он только проверяет «дорогу» и гейты.

### 4.4 Полный прогон (референс)

1. Preflight по всем 4 брокерам — `--broker Aedes|Mosquitto|ActiveMQ|EMQX`.
2. Guarded-пилот: `--max-runs 1` на каждом брокере, чтобы поймать гейты до полной кампании.
3. Full baseline: `matrix` по всей кампании (288 ключей). Ожидается десятки часов — 288 × (5+30+5+drain) секунд × 4 брокера с переключениями.
4. `aggregate` → `broker-observations.v3.json`.
5. Передача в `mqtty calibrate` → `broker-calibration.v3.json`.

---

## 5. Матрица 288 точек

Полный набор ключей определяется `CampaignDefinition.Expand()` и `Validate()` (`Campaign/CampaignDefinition.cs`):

```
288 = 4 брокера × 2 размера (16 / 256 B) × 3 QoS (0/1/2) × 4 издатели (1/16/64/128) × 3 повтора
```

- Формат ключа: `<Broker>.m<MessageBytes>.q<Qos>.p<Publishers>.r<Repeat>` (напр. `Mosquitto.m256.q2.p64.r2`).
- Порядок — по SHA-256 (стабильный между версиями рантайма), брокеры группируются для последовательности; `seed = 20260909`.
- `Validate()` **жёстко** требует ровно это разбиение и seed — иначе кампания отклоняется. Изменение размерности (например, другой набор издателей) требует отдельной схемы/кампании.
- Оконна по времени на ключ (base, `benchmark-campaign.v3.json`): `warmup 5 c`, `measurement 30 c`, `cooldown 5 c`, `drain ≤ 30 c`, `quiet 2 c`.
- После агрегации получается **96 точек** (по одной на комбинацию брокер/размер/QoS/издатели), а 3 повтора дают статистику (mean, σ, Student-t CI95, df=2).

---

## 6. Какие метрики собираем

Метрики собираются на **трёх независимых уровнях** и не смешиваются: (a) протокол, (b) загрузочный учёт на генераторе (Windows), (c) телеметрия контейнера (Linux). Плюс **провенанс** — хэши и идентификаторы.

### 6.1 Протокольный preflight (`ProtocolPreflight`)

Для каждого брокера до нагрузки:

- **CONNECT / SUBSCRIBE / PUBLISH / DELIVERED** для QoS 0, 1 и 2 (каждый шаг записывается: `ProtocolProbe`).
- **20 RTT-зондов** (QoS0 echo-loopback) → медиана = `rttBaselineMs` (используется как базовая задержка канала).
- **`$SYS/broker/version`** (подписка до 10 c); отсутствие/отказ → `null`/`unavailable`, ошибка фиксируется.
- **Захват среды** контроллера: OS, кол-во процессоров, .NET-рантайм, архитектура, hostname, частота Stopwatch (`BenchmarkEnvironment`).

### 6.2 Загрузочные метрики одного измерения (`MeasurementSummary`)

Ведёт `DeliveryLedger`, снимок — `Snapshot(qos, actualSeconds)`. Для каждого ключа:

| Поле | Смысл |
|---|---|
| `AttemptedPublishes` | сколько publish-операций предпринято |
| `CompletedPublishes` | сколько завершено успешно (QoS1/2 — с ack) |
| `FailedPublishes` | сколько упало (с кодом ошибки) |
| `UniqueDeliveries` | уникальные доставленные ID |
| `DuplicateDeliveries` | повторные доставки того же ID |
| `UnexpectedDeliveries` | доставки неизвестных ID |
| `DeliveredAfterFailedPublish` | доставлено после неудачного publish (важно для QoS) |
| `CompletedIdsDelivered` | сколько завершённых ID действительно доставлено |
| `ActualMeasurementSeconds` | фактическая длительность измерительного окна |
| `PublishLatencyMs` | квантили задержки (QoS1/2; для QoS0 — `null`) |
| `LatencyStatus` | `notApplicable` / `unobserved` / `observed` |
| `ErrorReasons` | словарь «код ошибки → количество» |

Производные (`MeasurementSummary`):

```
AttemptedRps        = AttemptedPublishes / ActualMeasurementSeconds
CompletedRps        = CompletedPublishes / ActualMeasurementSeconds
PublishFailureRate  = FailedPublishes / AttemptedPublishes
DeliveryLossRate    = 1 - CompletedIdsDelivered / CompletedPublishes
```

Правила: каждый publish имеет **ровно одну попытку и один исход** (дубликаты идентичности запрещены); QoS0 латентность publish — «not applicable»; для QoS1/2 при Ok>0 квантили обязательны.

Drain-политика (`DrainPolicy.Decide`) заканчивает измерение по первому: `allCompletedDelivered` → `quietPeriod` (2 c без новых доставок) → `timeout` (30 c). Причина и длительность drain фиксируются.

### 6.3 Телеметрия контейнера брокера (`telemetry.sh`)

Sampler запускается внутри контейнера и пишет NDJSON (`telemetry.ndjson`) по **монотонным дедлайнам** `/proc/uptime` (не зависит от времени round-trip через SSH/TCP). Один sample содержит:

| Поле | Источник | Смысл |
|---|---|---|
| `timestamp` | `date -u` | UTC-метка |
| `monotonicSeconds` | `/proc/uptime` | монотонная метка (базовая для сверки) |
| `processRssBytes` | `/proc/<pid>/status` (VmRSS) | RSS брокер-процессов (`node`/`java`/`mosquitto`/`beam.smp`) |
| `processIds` | `/proc/[0-9]*` | список PID брокер-процессов |
| `cgroupMemoryCurrentBytes` | `/sys/fs/cgroup/memory.current` | текущая память контейнера |
| `cgroupCpuAndThrottleBase64` | `/sys/fs/cgroup/cpu.stat` | CPU и **throttling** (cgroup v2) |
| `diskIoBase64` | `/sys/fs/cgroup/io.stat` | disk I/O контейнера |
| `networkBase64` | `/proc/net/dev` | сетевые счётчики (host-shared) |
| `connectionsBase64` | `/proc/net/tcp` + `/proc/net/tcp6` | таблица TCP-соединений (host-shared) |

Скоупы зафиксированы в `TelemetryManifest`: `cpuScope = container-cgroup`, `networkScope = host-shared`. `StandSession.CaptureAsync` отклоняет захват, если samples < секунды или скоупы не совпадают. Fence (`workload-end-fence.json`) доказывает, что последний sample позже завершения нагрузки и `containerId` совпадает во всех записях.

### 6.4 Метрики генератора нагрузки (Windows, `LoadGeneratorGuard`)

Самостоятельный уровень, **не влияет** на `broker-observations.v3.json`, но решает приёмку попытки. Сэмплер читает Windows-счётчики каждую секунду:

- **Системный CPU**: `100 * (Δkernel + Δuser − Δidle) / (Δkernel + Δuser)` (`GetSystemTimes`; kernel включает idle).
- **Сеть**: `RX bits/s`, `TX bits/s` из NIC-counters; утилизация = `(RX + TX) / linkSpeed * 100` (консервативно, даже на full-duplex).
- **Hardware evidence**: `Get-NetAdapter -IncludeHidden` → `HardwareInterface`, `Virtual`, InterfaceIndex, InterfaceGuid. Отклоняются виртуальные/tunnel/loopback адаптеры и смена маршрута/скорости.

Оценка (`LoadGeneratorAssessment.Evaluate`) идёт по **каждому интервалу**, пересекающему измерение (не по среднему):

- интервал > 1.25 s → «нехватка покрытия»;
- падение/переполнение счётчиков, пересечение чтений, смена скорости → отказ;
- `CPU > 70%` → отказ;
- `networkPercent > threshold` → отказ.

Пороги: `CPU ≤ 70%` всегда; сеть — `networkThresholdPercentByBroker`: Aedes `null`/85% (информационно), Mosquitto/ActiveMQ/EMQX `80%` (см. `benchmark-campaign.v3.json` и `campaign-pilot.local.json`).

Артефакты уровня: `load-generator-started.json`, `load-generator-provenance.json`, `load-generator-samples.ndjson`, `load-generator-measurement-started.json`/`-ended.json`, `load-generator-summary.json`.

### 6.5 Провенанс и артефакты попытки

Каждая попытка (key + attempt number) складывается в собственный каталог с «запечатанной» совокупностью:

- `campaign.json`, `attempts.json` — идентичность кампании и журнал попыток.
- `inventory-check/stand.source.json` — точные байты инвентаря, сверенные с `standSha256`.
- `stand.selected.json` — инвентарь с выбранным брокером.
- `run-provenance.json` — imageId, effectiveConfigs, хэши входных `start-input`, `startedAt`.
- `controller-<action>.json` — выход/ошибки каждого действия контроллера.
- `protocol.json` — протокольный preflight.
- `publishes.ndjson`, `deliveries.ndjson` — пооперационный учёт (event/id/latency).
- `telemetry.ndjson`, `telemetry-ready.json`, `measurement-started.json`, `workload-end-fence.json`, `sampler-ended.json`, `telemetry-manifest.json`.
- `preflight.json`, `sha256.json` — итог попытки + хэш дерева каталога.
- `load-generator-*.json/.ndjson` — уровень генератора.

Идентичность кампании (`CampaignIdentity`) включает: `campaignId`, `configSha256`, `standSha256`, `deploymentMode`, `cpuMode`, git-ревизию `MqttBenchmark`. `aggregate` отвергает смешанные идентичности и требует 288/288.

---

## 7. Пререквизиты

### 7.1 Контроллер / генератор нагрузки (Windows)

- **OS**: Windows (Windows-счётчики и hardware evidence доступны только на Windows; production-матрица на других ОС не поддерживается и «fail-closed»).
- **.NET 6 SDK** — для `dotnet run --project src/EntityFX.MqttBenchmark.Cli`.
- **PowerShell (pwsh)** — для `BrokerStand.ps1`.
- **Docker CLI 29.x + Compose-плагин** — в PATH или заданы через `--docker-executable`.
- **Настроенный Docker context** на хост брокеров (SSH-контекст, например `mqtty-stand`) **или** доступный TCP-relay (см. раздел 8).
- **Физическая сетевая карта** на маршрут к брокеру: `HardwareInterface=true`, `Virtual=false`, не loopback/tunnel, известная `linkSpeed`. (Виртуальные NIC/VPN/туннели отклоняются `WindowsLoadGeneratorCounters`.)
- **Достаточные ресурсы генератора** (handoff): ≥ 8 физических ядер, ≥ 16 GiB RAM, физический проводной маршрут (рекомендуется ≥ 1 GbE), свободное место на диске для raw-артефактов, **отключённый сон/hibernate**.
- **Чистое дерево** `MqttBenchmark` на закоммиченной ревизии (git-HEAD входит в идентичность кампании).

### 7.2 Хост брокеров (Linux)

- **OS**: Linux x86-64 с **cgroup v2** (ALT Workstation 11.1/p11; `telemetry.sh` читает `/sys/fs/cgroup/{cpu.stat,memory.current,io.stat}`).
- **Docker Engine + Compose** установлены и включены; пользователь (например, `gdc`) в группе `docker`.
- **SSH-доступ** по ключу (неинтерактивный) — для SSH-контекста.
- **Хост-сеть** доступна контейнерам (`network_mode: host`); порты MQTT (1883/2883/3883/4883) и management (8161/18083) не заняты.
- **`/proc/uptime`** доступен для монотонных дедлайнов и fence.
- **Один закреплённый CPU** (`cpuset 0`) и 4 GiB на контейнер — режим `singleCorePinned`.
- **Безопасность**: не выносить в артефакты/логи пароли, ключи, токены; реестровые образы только по проверенному SHA-256 digest.

### 7.3 Кастомные сборки (Aedes, ActiveMQ)

- Доступ к сборочным входам (`docker/brokers/Dockerfile.aedes`, `docker/brokers/activemq/Dockerfile`) и возможность `docker build` на целевом хосте.
- Запись локального image ID и хэшей сборочных входов (провенанс); `digest` для кастомных брокеров остаётся пустым.
- Опционально `MQTTY_BUILD_HTTP_PROXY` / `MQTTY_BUILD_NO_PROXY` — только build-args, не попадают в runtime-образ.

### 7.4 Транспорт (SSH-контекст или relay)

- **Основной**: SSH-контекст Docker CLI, неинтерактивный вход по ключу, рабочая связь до Unix-сокета `/var/run/docker.sock`.
- **Альтернативный**: TCP-relay на `23762` (контейнер `relay-tc`) с коалесцингом `101`+первого кадра. Подробности и текущее состояние — раздел 8.

---

## 8. Транспорт: TCP-relay и коалесцинг (текущее состояние)

При работе **не через SSH-контекст**, а через TCP-relay к Unix-сокету Docker daemon (контейнер `relay-tc`, порт `23762`) обнаружен специфический дефект взаимодействия Go docker CLI:

- Напрямую (Unix-сокет) ответы `HTTP/1.1 101 UPGRADED` и первый stdout-кадр приходят **в одном** read-пакете.
- Через TCP-relay они разделяются ~50 мс, и Go CLI закрывает соединение **до** получения данных (потеря первого кадра).

**Обход** — `.runtime/relay-coalesce.js`: детектит заголовок `101` (regex `/^HTTP\/1\.[01] 101/` + `upgrade`), буферит его и следующие чанки до таймаута idle (`IDLE_FLUSH_MS=150`, или до EOF) и выпускает **одним** пакетом, воспроизводя поведение Unix-сокета. Входящий (client→upstream) поток не коалесцируется — интерактивный stdin идёт сразу.

Статус (на момент составления документа):

- Однократные exec (`sha256sum`, `echo`) — работают через relay.
- Многострочные команды работают при наличии задержки (например, `sleep`) до вывода.
- `telemetry.sh` через relay **не завершён**: в контейнере Mosquitto `sh -c` с `set -eu` падает (вероятно, отсутствующие cgroup-файлы или busybox-синтаксис) → 0 строк.
- Следующие шаги: диагностика окружения контейнера (`ls /sys/fs/cgroup`, версия `sh`), правка `telemetry.sh` на graceful-обход (убрать `set -eu` или добавить `2>/dev/null || true`), повторная проверка через relay.
- **Обязательное требование**: relay обязан запуститься **внутри контейнера** (`relay-tc`), т.к. фоновые процессы на хосте убиваются PAM-модулями при закрытии сессии.

### 8.1 Чек-лист «проверяем relay»

```sh
# 1) Однократные exec через прямой сокет и через relay должны совпасть
timeout 30 docker exec mosquitto sh -c 'echo ok'
timeout 30 docker -H tcp://127.0.0.1:23762 exec mosquitto sh -c 'echo ok'

# 2) Многострочные с задержкой (успешный кейс через relay)
timeout 30 docker -H tcp://127.0.0.1:23762 exec mosquitto sh -c 'sleep 1; echo A; echo B'

# 3) Текущая проверка счётчика monotonicSeconds (в .runtime/tele-test.sh)
```

---

## 9. Карта файлов и быстрые команды

### 9.1 Основные файлы стенда

| Назначение | Путь |
|---|---|
| Инвентарь брокеров (закоммиченный шаблон) | `MqttBenchmark/config/broker-stand.v1.json` |
| Локальная версия (на реальном хосте) | `MqttBenchmark/config/broker-stand.local.json` (git-ignored) |
| Настройка кампании v3 | `MqttBenchmark/config/benchmark-campaign.v3.json` |
| Пилот-кампания | `MqttBenchmark/config/campaign-pilot.local.json` |
| PowerShell-контроллер | `MqttBenchmark/scripts/BrokerStand.ps1` |
| CLI (preflight/matrix/aggregate) | `MqttBenchmark/src/EntityFX.MqttBenchmark.Cli/Program.cs` |
| Compose 4 брокеров | `MqttBenchmark/docker/brokers/compose.yml` |
| Sampler телеметрии | `MqttBenchmark/docker/brokers/telemetry.sh` |
| Dockerfile Aedes | `MqttBenchmark/docker/brokers/Dockerfile.aedes` |
| Dockerfile ActiveMQ | `MqttBenchmark/docker/brokers/activemq/Dockerfile` |
| Документация | `MqttBenchmark/docker/brokers/README.md`, `MqttBenchmark/docs/campaign-v3.md`, этот файл |
| Handoff/текущее состояние | `CONTINUATION-HANDOFF.md` |
| Relay (workaround) | `.runtime/relay-coalesce.js` |
| Relay-тест | `.runtime/tele-test.sh` |

### 9.2 Быстрые команды (контроллер)

```powershell
# Проверить инвентарь
./scripts/BrokerStand.ps1 -Action validate -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001

# Собрать кастомные (Aedes/ActiveMQ) и записать провенанс
./scripts/BrokerStand.ps1 -Action pull -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001

# Поднять активного брокера
./scripts/BrokerStand.ps1 -Action start -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001

# Health/clock
./scripts/BrokerStand.ps1 -Action health -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001
./scripts/BrokerStand.ps1 -Action clock  -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001

# Захват 10 c телеметрии
./scripts/BrokerStand.ps1 -Action capture -CaptureSeconds 10 -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001

# Стоп/сброс
./scripts/BrokerStand.ps1 -Action stop  -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001
./scripts/BrokerStand.ps1 -Action reset -ConfigPath config/broker-stand.local.json -OutputDirectory artifacts/stand-001
```

### 9.3 Быстрые команды (кампания)

См. раздел 4.2. Ключевая последовательность: `preflight` → `matrix` (или `matrix --max-runs 1`) → `aggregate`.
