Write-Host "Run 10 seconds"

Push-Location "EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Write-Host "Run Bomber - 256 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m256_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Write-Host "Run Bomber - 1 kb - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m1024_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=1024 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=1024"

Pop-Location

Push-Location "EntityFX.MqttBenchmark\bin\Release\net6.0\"

Write-Host "Run MqttBenchmark - 256 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:10"

Write-Host "Run MqttBenchmark - 1 kb - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:10"

Pop-Location


Write-Host "Run 210 seconds"


Push-Location "EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Write-Host "Run Bomber - 256 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m256_t210.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Write-Host "Run Bomber - 1 kb - 210 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m1024_t210.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=1024 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=1024"

Pop-Location

Push-Location "EntityFX.MqttBenchmark\bin\Release\net6.0\"

Write-Host "Run MqttBenchmark - 256 b - 210 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=1024 Tests:Settings:TestMaxTime=00:02:30"

Write-Host "Run MqttBenchmark - 1 kb - 210 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=1024 Tests:Settings:TestMaxTime=00:02:30"

Pop-Location