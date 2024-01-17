Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "TemplateFiles:Scenarios=templates/scenarios_m256_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "TemplateFiles:Scenarios=templates/scenarios_m1024_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=1024"

Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "TemplateFiles:Scenarios=templates/scenarios_m256_t210.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "TemplateFiles:Scenarios=templates/scenarios_m1024_t210.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=1024"



