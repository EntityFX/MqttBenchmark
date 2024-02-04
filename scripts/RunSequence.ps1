Get-Date 
Write-Host "Run 10 seconds"

Push-Location "EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 16 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m16_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=16 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=16"

Get-Date
Write-Host "Run Bomber - 256 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m256_t10.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Pop-Location

Push-Location "EntityFX.MqttBenchmark\bin\Release\net6.0\"

Get-Date
Write-Host "Run MqttBenchmark - 16 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=16 Tests:Settings:TestMaxTime=00:00:10"

Get-Date
Write-Host "Run MqttBenchmark - 256 b - 10 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:10"

Pop-Location

Get-Date 
Write-Host "Run 180 seconds"


Push-Location "EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 16 b - 180 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m16_t180.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=16 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=16"

Get-Date
Write-Host "Run Bomber - 256 b - 180 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false TemplateFiles:Scenarios=templates/scenarios_m256_t180.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Pop-Location

Push-Location "EntityFX.MqttBenchmark\bin\Release\net6.0\"

Get-Date
Write-Host "Run MqttBenchmark - 16 b - 180 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=true Tests:Settings:MessageSize=16 Tests:Settings:TestMaxTime=00:02:00"

Get-Date
Write-Host "Run MqttBenchmark - 256 b - 180 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:02:00"

Pop-Location

Get-Date 