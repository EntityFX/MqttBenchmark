Get-Date 
Write-Host "Run 30 seconds"


Push-Location "..\src\EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false -c appsettings.mos_emqx.c1__24.json  TemplateFiles:Scenarios=templates/scenarios_m256_t30.template.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-1c}-{clients}cl:MessageSize=256 ScenarioNameTemplates:benchmark-qos{qos}-{servers-mc}-{clients}cl:MessageSize=256"

Pop-Location

Push-Location "..\src\EntityFX.MqttBenchmark\bin\Release\net6.0\"

Get-Date
Write-Host "Run MqttBenchmark - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "Tests:InParallel=false -c appsettings.mos_emqx.c1__24.json Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:30"

Pop-Location

Get-Date 