# MqttBenchmark

Измерение MQTT-брокеров и воспроизводимые кампании **v3** для калибровки MqttY: 288 успешных запусков, 96 наблюдаемых точек, exact-ID delivery accounting, immutable retry/resume и SHA-256 provenance.

Начните с [индекса документации](docs/README.md): установка без Docker, подготовка Ubuntu/ALT/Windows, локальный и SSH-стенд, ручной и управляемый запуск, архитектура и метрики. Aedes/Mosquitto/ActiveMQ Classic запускаются непосредственно в Linux/Windows; EMQX — в Linux, на Windows внутри Ubuntu WSL2. Статус фактических проверок отделён от поддерживаемого маршрута.

Из корня проекта в PowerShell 7, после подготовки брокера и spec:

```powershell
./scripts/NewNativeStand.ps1 -BrokerSpecPath config/brokers.spec.local.json `
  -StandPath config/broker-stand.local.json -CampaignPath config/campaign.local.json
./scripts/RunNativeCampaign.ps1 -ConfigPath config/campaign.local.json `
  -StandPath config/broker-stand.local.json -CampaignDirectory artifacts/pilot-001 -Pilot
```

Pilot не даёт полного набора для калибровки. Запуск требует чистых tracked исходников; примеры настройки и ручной CLI-эквивалент — в [локальном руководстве](docs/local-stand.md). Контейнерный config/broker-stand.v1.json не является native-инвентарём.

[Переносимый пакет](DEPLOYMENT.md) · [Калибровка MqttY](../mqtty/doc/calibration-v3.md) · [Исторические результаты](../diss/doc/results-and-evidence.md).

[Последовательная двухмашинная серия](docs/two-machine-campaign.md): отдельные Markdown/HTML-отчёты, независимый контроль и перенос каждой sealed attempt на evidence disk.
