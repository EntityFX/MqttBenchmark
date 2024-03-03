Get-Date 
Write-Host "Run 30 seconds"


Push-Location "..\src\EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 16 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-host}-{clients}cl:MessageSize=16"

Get-Date
Write-Host "Run Bomber - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-host}-{clients}cl:MessageSize=256"

Get-Date
Write-Host "Run Bomber - 1 mb - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json ScenarioNameTemplates:benchmark-qos{qos}-{servers-host}-{clients}cl:MessageSize=1048576"


Pop-Location

Push-Location "..\src\EntityFX.MqttBenchmark\bin\Release\net6.0\"

Get-Date
Write-Host "Run MqttBenchmark - 16 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json Tests:InParallel=false Tests:Settings:MessageSize=16 Tests:Settings:TestMaxTime=00:00:30"

Get-Date
Write-Host "Run MqttBenchmark - 256 b - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json Tests:InParallel=false Tests:Settings:MessageSize=256 Tests:Settings:TestMaxTime=00:00:30"

Get-Date
Write-Host "Run MqttBenchmark - 1 mb - 30 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.exe" -Wait -ArgumentList "-c appsettings.c1_2_4_8_12_16_24.activemq.json Tests:InParallel=false Tests:Settings:MessageSize=1048576 Tests:Settings:TestMaxTime=00:00:30"

Pop-Location

Get-Date 