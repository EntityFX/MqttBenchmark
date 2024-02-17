Get-Date 
Write-Host "Run 30 seconds"


Push-Location "..\src\EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.emqx_mosquitto.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-host}-{clients}cl:MessageSize=256"

Pop-Location

Push-Location "..\src\EntityFX.MqttBenchmark\bin\Release\net6.0\"

Get-Date
Write-Host "Run MqttBenchmark - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.emqx_mosquitto.json Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:30"

Pop-Location

Get-Date 