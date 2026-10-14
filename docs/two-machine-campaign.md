# Последовательная серия с отдельными отчётами

Контроллер Windows выполняет нагрузку и MqttY, Linux запускает native-брокеры.
`scripts/campaign/prepare.py` создаёт отдельные pinned входы каждого брокера,
`scripts/campaign/run.py` выполняет Aedes → Mosquitto → ActiveMQ Classic → EMQX,
затем смешанную сеть. JSON серии хранит точные пути, commits фиксируются CLI.

Перед подготовкой нужны .NET 6, PowerShell 7, отдельный SSH-ключ и установленное
окружение брокеров. На Linux инструменты лежат на evidence disk; проектные
скрипты не устанавливают и не обновляют брокеры в ходе измерений.
Инвентарь задаёт `linuxLaunchMode=systemdUser`: transient user service сохраняет
брокер после закрытия SSH-сеанса, применяет CPU affinity до создания потоков и
MemoryMax. Требуются пользовательский systemd/D-Bus и Python3 с dbus-python.
Переменные окружения передаются в память manager через D-Bus, без env-файлов
или значений в argv. Фактические ограничения проверяются в procfs/cgroup.
`scripts/campaign/requirements.txt` задаёт зависимости генератора документов.

```powershell
python scripts/campaign/prepare.py --series artifacts/two-machine-<new-id> --identity-file C:/Users/<user>/.ssh/mqtty_two_machine
python scripts/campaign/run.py --series artifacts/two-machine-<new-id>
python scripts/campaign/run.py --series artifacts/two-machine-<same-id> --resume
python scripts/campaign/run.py --series artifacts/two-machine-<same-id> --verify-only
```

Подготовка storage выполняется отдельно: `configure-storage.py` создаёт отдельную
учётную запись и ограниченный controller IP SMB share. Пароль читается из stdin;
существующие учётные записи и службы сохраняются. На контроллере credential
должен храниться в Windows Credential Manager, а не в JSON серии.

## Порядок и статистика

На каждого брокера: pilot, 72 calibration measurements, immutable profile,
72 calibration fidelity simulations, 72 independent real measurements,
72 holdout simulations, 72 homogeneous relay simulations. После четырёх брокеров:
72 mixed simulations. Payload 16/256, QoS0/1/2, publishers1/16/64/128, repeats3;
native seeds20260909/20260910, simulation seeds42/43/44. Pilot — три QoS с
128 publishers; его результаты не используются для калибровки.

Натурные длительности5/30/5с, drain30с; simulation tick0.1мс, warmup5с,
measurement30с, drain30с. Transport фиксируется из calibration до holdout:
one-way transfer ticks округляются из половины среднего MQTT echo RTT.
Это приближение, включающее echo overhead брокера; оно не является точной
идентификацией сетевого пути. Capacity125000000 bytes/s соответствует
проверенному линку1Gbps; queueCapacity100000 — допущение.

Однородный relay offeredRps=4×attemptedRps; mixed=4×minimum attemptedRps
четырёх соответствующих точек. Это внешняя суммарная нагрузка, внутренняя
пересылка дополнительна. Пороги fidelity применяются только к одиночному
BrokerFidelity; для relay reference остаётся диагностикой.

## Ограничение дискового объёма и доказательства

`matrix --archive-root <external-path>` переносит каждую sealed attempt в ZIP
после завершения workload, telemetry, guard и cleanup. Хэш архива, исходный seal
и все entries проверяются до удаления подробных локальных файлов. На Windows
остаются started/result/seal/archive receipt. `resume` и `aggregate` проверяют
архивы, сохраняют историю ошибок и исходный retry budget. SHA-256 вычисляется
потоком, без загрузки больших журналов целиком в RAM.

В серии Windows требуется≥10GiB перед командой; watchdog прерывает при<6GiB.
CLI не начинает следующую attempt при<8GiB. На evidence disk требуется≥20GiB,
watchdog прерывает при<12GiB. Проверяется точный mount TARGET/SOURCE/FSTYPE.
На системных разделах не создаются полные копии verification; восстановление
и повторный aggregate идут на hd250. Смена диска, обрыв связи, checksum failure
или нехватка места останавливают серию и сохраняют доказательства.

Повторная агрегация восстановленных raw обязана совпасть по scientific points
и campaign identity. InputSha256 отличается для compact/полного представления;
оба observations и их хэши сохраняются в archive-verification.json.

Пять Markdown/HTML документов обновляются по этапам. HTML включает растровые
графики внутрь документа; PNG/SVG/CSV/JSON также доступны отдельно. CPU/RSS
отчёта относятся к полному capture envelope, что явно подписано. Receiver
counters симуляции исключают warmup baseline, но сообщения warmup in-flight
могут пересечь границу измерения. Недостающие counters не заменяются нулями.

При выходе experiment3 сохраняется провал fidelity и выполняется остальная
матрица. Выходы1/2 — техническая остановка, перед resume требуется устранить
причину. Не изменять код, профили или входы ради прохождения критериев.
