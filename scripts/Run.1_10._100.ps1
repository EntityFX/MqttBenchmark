Get-Date 
Write-Host "Run 180 seconds"


Push-Location "EntityFX.MqttBenchmark.Bomber\bin\Release\net6.0\"

Get-Date
Write-Host "Run Bomber - 256 b - 60 s"
Start-Process -FilePath ".\EntityFX.MqttBenchmark.Bomber.exe" -Wait -ArgumentList "InParallel=false -c appsettings.c1_10_100.json"

Pop-Location

Get-Date 