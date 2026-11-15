# Реализация четырёхброкерного стенда и обработка данных

## 1. План реализации четырёхброкерного стенда

### Ограничения

- Работа в существующих ветках `codex/dissertation-readiness` репозиториев `MqttBenchmark` и `mqtty`; сохранять нерелевантные изменения и коммитить каждый репозиторий независимо.
- TDD для production behavior: добавлять падающий поведенческий тест, RED, реализовать минимальное изменение, GREEN. Конфигурация и человеческая документация не требуют тестов изменения.
- Никогда не хранить учётные данные в исходниках, манифестах, логах, отчётах или аргументах команд. Существующие открытые учётные данные в `infra-config.json` заменить на переменные окружения/секреты.
- Raw-артефакты бенчмарка и fidelity остаются локальными и не коммитятся. Коммитить конфиги, манифесты, реестр SHA-256, агрегаты, observations/калибровку, компактные сводки/логи и 대표тельные relay-бандлы.
- Не изобретать успешные прогоны. Отсутствующий брокер, Docker, management endpoint или эквивалентный ресурс — записанный блокер.
- ActiveMQ означает ActiveMQ Classic, никогда Artemis. Плавающие теги образов запрещены; каждый брокер требует неизменяемый дайджест.
- Результаты агрегируются только в рамках одного deployment mode и одного CPU mode. `singleCorePinned` — обязательный базлайн; `hostAllCores` — отдельная опциональная кампания.

### Task 1: Воспроизводимый брокер-стенд и контроллер

В `MqttBenchmark` добавить `docker/brokers/compose.yml` с взаимоисключающими профилями для Aedes, Mosquitto, ActiveMQ Classic и EMQX; одноконтейнерные порты MQTT: Aedes 1883, Mosquitto 2883, ActiveMQ 3883, EMQX 4883; распределённые хосты используют порт 1883. Отключить persistence, выровнять логирование/restart/memory. Management порты привязать только к control interface.

Добавить явные конфигурации брокеров с отключённым persistence, политикой `unless-stopped`, лимитом 512 MiB, CPU set `0` и ограниченным локальным логированием. Aedes строится из Node 20 Alpine с закреплённым дайджестом, Aedes 1.1.2, закоммиченный lockfile.

Добавить `config/broker-stand.v1.json` и `scripts/BrokerStand.ps1`. Контроллер реализует `validate`, `pull`, `start`, `health`, `reset`, `capture`, `stop`; валидирует совместимость инвентаря/кампании; захватывает один образец телеметрии, логи, версии и SHA-256 хэши конфигураций. Заменить открытые учётные данные Bomber на ссылки на переменные окружения.

Добавить поведенческие тесты для валидации схемы/инвентаря, эксклюзивной загрузки брокера, CPU mode, закреплённых дайджестов, валидации контроллера, захвата версии/конфигурации и семантики очистки. Закоммитить задачу и отчитаться о RED/GREEN.

### Task 2: Схема кампании v3 и неизменяемый раннер

В `MqttBenchmark` добавить `config/benchmark-campaign.v3.json` для 4 брокеров, payload 16/256 байт, QoS 0/1/2, publishers 1/16/64/128, три повтора, 5 с прогрева, 30 с измерения, 5 с охлаждения, 30 с drain timeout, 2 с quiet period, seed 20260909. Расширить CLI командами `preflight`, `matrix --resume`, `aggregate`.

Каждый ровный размер полезной нагрузки содержит детерминированный 16-байтовый ID. Записывать попытки, завершённые публикации, причины ошибок, уникальные/дублирующие/неожиданные доставки, доставку после неудавшейся публикации. Использовать фактическую длительность измерения для attempted/completed RPS и формул отказа/потерь. QoS 0 latency не применима; QoS 1/2 используют end-to-end completion latency. Drain завершается после доставки всех завершённых ID или по quiet period/timeout. Попытки неизменяемы; resume пропускает успешные ключи, разрешает две дополнительные попытки с отдельными артефактами и останавливает кампанию после третьей неудачной попытки для ключа.

Preflight выполняет CONNECT/SUBSCRIBE/PUBLISH для QoS 0/1/2, 20 RTT-проб, `$SYS/broker/version`, захват окружения и манифест телеметрии. Aggregate только при полном покрытии 288/288 и вычисляет Student-t CI95. Эмитит `broker-observations.v3.json` с provenance. Добавить поведенческие unit/интеграционные тесты для детерминированного порядка, точных payload ID, drain, неизменяемого retry/resume, статистики, совместимости агрегации, вывода preflight.

### Task 3: Калибровка MqttY v3 и топология fidelity

В `mqtty` добавить команду `calibrate`, потребляющую `broker-observations.v3.json` и записывающую `broker-calibration.v3.json`. Schema v3 хранит AttemptedRps, target CompletedRps, условные publish-failure и delivery-loss rates, наблюдаемую/обрабатывающую задержку, RTT baseline и provenance. Вычислять `Fconditional = (Ftarget - Frate) / (1 - Frate)` и отклонять точки, где `Frate > Ftarget`. Вычитать один RTT из задержки QoS 1 и два RTT из QoS 2, обрезанных в ноль; QoS 0 остаётся не применимой.

Исправить загрузку внешнего профиля так, чтобы точные байты, хэшированные командой, парсились раннером. Добавить `ExperimentTopologyMode = BrokerFidelity | MqttRelay` и `ProfileClientCountMode = Publishers | ConnectedClients`. BrokerFidelity создаёт N publishers плюс один subscriber. MqttRelay сохраняет топологию relay, но выбирает клиентов профиля согласно настроенной семантике. Не заменять встроенный четырёхброкерный профиль.

Добавить поведенческие тесты для формул калибровки/provenance, идентичности байтов внешнего профиля, обоих режимов topology/client-count и детерминированного проигрывания.

### Task 4: Реальное исполнение, протокол и документация диссертации

С контроллера Windows валидировать/развёртывать/проверять здоровье всех доступных брокеров. Существующие endpoints Aedes `localhost:1883` и Mosquitto `10.10.157.111:9883` — только пилотные endpoints, если не доказаны сравнимые ресурсные манифесты. Запустить preflight и короткий пилот для каждого доступного брокера/QoS; затем запустить 288-точечный single-core baseline только если у всех четырёх брокеров эквивалентные закреплённые ресурсы и зелёный preflight/pilot. Запустить 96 точек BrokerFidelity для трёх seed и репрезентативное MqttRelay smoke покрытие. Указанное число 12 conflicts с `4 брокера × 2 payload × 3 QoS = 24`; полное Cartesian покрытие — 24 и является обязательной интерпретацией. Опциональная all-core кампания остаётся отдельной.

Создать `diss/doc/dissertation-readiness/task-8-real-broker-validation-report.md`; обновить `plan.md`, `project-reviews-and-skills.md` и архитектурные главы 06/10/12. Включить окружение/манифесты, транскрипт команд, хэши, покрытие, пороги fidelity (CompletedRps ≤15 %, p99 QoS1/2 ≤20 %, failure/loss абсолютно ≤0.05, QoS0 latency не применима), ограниченную статистическую мощность от трёх повторов, честные блокеры и рекомендуемые улучшения MqttY/MqttBenchmark. Закоммитить документацию в репозиторий, которому она принадлежит, и отчитаться о верификации.

## 2. Обработчик данных теста

0. Установить зависимости:

```
pip install -r requirements.txt
```

или

```
python -m pip install -r requirements.txt
```

1. Заменить названия входных файлов:

```python
# файлы с данными нагрузки на CPU из Prometheus
cpu_usage_files = [
    "cpu.nb.m256.2024-03-13T03_07_03__10.114.22.10.json",
    "cpu.nb.m256.2024-03-13T03_07_03__10.114.22.50.json",
]

# файлы с данными теста
result_files = [
    "results.nb.m256.2024-03-13T03_07_03.csv",
]
```

2. Запустить `datamerge.py` или `datamerge.ipynb`

3. Результаты обработки сохраняются в файлы `q_#.csv`, где `#` — уровень QoS

4. Для последующей обработки импортировать файлы `q_#.csv` в таблицу Excel `MqttBenchmarkResults.xlsx`

## 3. Broker stand (Docker)

Run the PowerShell controller against a Linux Docker context with cgroup v2. All broker traffic uses direct host networking. `singleCorePinned` selects one CPU; `hostAllCores` requires an empty CPU set. Both modes enforce 4 GiB of container memory. In `singleHostSequential`, start removes other containers carrying the stand label before loading the selected broker.

From the MqttBenchmark directory:

```powershell
$inventory = 'config/broker-stand.v1.json'
$output = 'artifacts/broker-stand/run-001'
./scripts/BrokerStand.ps1 -Action pull -ConfigPath $inventory -OutputDirectory $output
./scripts/BrokerStand.ps1 -Action start -ConfigPath $inventory -OutputDirectory $output
./scripts/BrokerStand.ps1 -Action health -ConfigPath $inventory -OutputDirectory $output
./scripts/BrokerStand.ps1 -Action capture -CaptureSeconds 10 -ConfigPath $inventory -OutputDirectory $output
./scripts/BrokerStand.ps1 -Action stop -ConfigPath $inventory -OutputDirectory $output
./scripts/BrokerStand.ps1 -Action reset -ConfigPath $inventory -OutputDirectory $output
```

Set `dockerContext`, `controlInterface`, MQTT/management URIs, `activeBroker`, and its `loaded` flag in the inventory for the target host. Distributed inventories select the active broker's own `dockerContext` and use MQTT port 1883. ActiveMQ's MQTT and HTTP listeners bind the control address. EMQX's dashboard, Erlang distribution, and internal RPC listeners also bind that address.

`pull` builds Aedes and ActiveMQ and records local image IDs and build-input hashes under `build/`. Keep custom `digest` fields null: local image IDs are not registry manifest digests. Deployment, health, and capture verify this provenance, including current source hashes. Stop/reset remain available after source changes. Registry images require verified SHA-256 digests.

Builds may optionally use `MQTTY_BUILD_HTTP_PROXY` and `MQTTY_BUILD_NO_PROXY` from the controller process environment. Both custom builds use host networking. Compose stores environment references, not proxy values, and Docker's predefined proxy build arguments are not added to the runtime image. Direct internet access needs no proxy setting.

The Compose document uses JSON syntax, which is valid YAML. The controller selects a single service and transfers the exact effective configuration through encoded environment values and a container entrypoint. There are no host bind mounts, including when the client is Windows and the daemon is remote Linux.

Choose a fresh output directory for each campaign run. Start saves the effective Compose/config files and `run-provenance.json`; capture verifies their deployed hashes without regenerating them. Telemetry appends NDJSON samples. A single container command schedules samples against `/proc/uptime` deadlines, independent of Docker/SSH round-trip time. CPU/throttle, memory, and disk I/O are container cgroup counters; RSS covers broker processes. Network and TCP tables are explicitly host-shared. `versions.json` identifies the running image and reports the actual broker and Node/Java runtime versions.

Application logging is warning/error to console, with bounded Docker local logs. ActiveMQ's Log4j and Java logging are configured together and its distribution JVM management agent is disabled. One-time distribution startup banners may still appear.