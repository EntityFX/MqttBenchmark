"""Create a controller series and prepare remote pinned broker copies."""
import argparse, datetime, json, pathlib, shutil, subprocess, sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, BROKERS
parser=argparse.ArgumentParser();parser.add_argument('--series',required=True);parser.add_argument('--identity-file',required=True);parser.add_argument('--cpu-threshold-percent',type=float,default=70)
args=parser.parse_args();root=pathlib.Path(args.series).resolve();benchmark=pathlib.Path(__file__).resolve().parents[2];mqtty=benchmark.parent/'mqtty'
if not 0<args.cpu_threshold_percent<=100:raise RuntimeError('CPU threshold must be in (0,100]')
if (root/'series.json').exists():raise RuntimeError('Use a fresh series root; inputs are immutable')
root.mkdir(parents=True,exist_ok=True);inputs=root/'inputs';inputs.mkdir()
archive_linux='/run/media/entityfx/hd250/mqtty-evidence/'+root.name
archive_windows=pathlib.Path(r'\\192.168.5.1\mqtty-evidence')/root.name
archive_windows.mkdir(exist_ok=True)
ssh='C:/Program Files/Git/usr/bin/ssh.exe';target='entityfx@192.168.5.1';identity=str(pathlib.Path(args.identity_file).resolve())
remote_repo='/home/entityfx/projects/mqtty-meta/MqttBenchmark'
transport=[ssh,'-i',identity,'-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',target]
source=(pathlib.Path(__file__).parent/'prepare-linux.py').read_bytes()
subprocess.run(transport+['cat > '+archive_linux+'/prepare-linux.py'],input=source,check=True)
subprocess.run(transport+['python3 '+archive_linux+'/prepare-linux.py --series '+archive_linux+' --repository '+remote_repo],check=True)
native={'host':'192.168.5.1','user':'entityfx','port':22,'repositoryRoot':remote_repo,'workRoot':archive_linux+'/worker',
        'pwshExecutable':'/run/media/entityfx/hd250/mqtty-evidence/tools/pwsh/pwsh','identityFile':identity,'sshExecutable':ssh,
        'knownHostsFile':str(pathlib.Path.home()/'.ssh/known_hosts')}
for broker in BROKERS:
    stand=json.loads((archive_windows/'inputs'/f'{broker}-stand.json').read_text())
    stand['nativeHost']=native;stand['dockerContext']='native-ssh-192.168.5.1'
    atomic_json(inputs/f'{broker}-stand.json',stand)
    campaign=json.loads((archive_windows/'inputs'/f'{broker}-calibration.json').read_text())
    campaign['cpuThresholdPercent']=args.cpu_threshold_percent
    atomic_json(inputs/f'{broker}-calibration.json',campaign)
    campaign['seed']=20260910;atomic_json(inputs/f'{broker}-validation.json',campaign)
    campaign.update(seed=20260908,messageBytes=[16],qos=[0,1,2],publishers=[128],repeats=1,warmupSeconds=1,measurementSeconds=3,cooldownSeconds=1)
    atomic_json(inputs/f'{broker}-pilot.json',campaign)
plan={'schemaVersion':1,'seriesId':root.name,'createdAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
      'benchmarkRepository':str(benchmark),'mqttYRepository':str(mqtty),
      'benchmarkCli':str(benchmark/'artifacts/portable-controller/bin/EntityFX.MqttBenchmark.Cli.dll'),
      'simulatorCli':str(benchmark/'artifacts/portable-simulator/EntityFX.MqttY.MqttRelay.App.dll'),
      'simulatorExample':str(mqtty/'doc/examples/broker-fidelity.v3.json'),
      'archiveLinuxRoot':archive_linux,'archiveWindowsRoot':str(archive_windows),'archiveMount':'/run/media/entityfx/hd250','archiveDevice':'/dev/sdc1',
      'sshExecutable':ssh,'identityFile':identity,'sshTarget':target,'expectedRealRuns':576,'expectedFidelityRuns':576,'expectedRelayRuns':360,
      'brokerOrder':list(BROKERS),'diskPolicy':{'windowsBeforeRunGiB':10,'windowsAbortGiB':6,'hd250BeforeRunGiB':20,'hd250AbortGiB':12}}
plan['cpuGuardPercent']=args.cpu_threshold_percent
plan['rawControllerRoot']=str(pathlib.Path.home()/'.mqtty-scratch'/root.name/'raw')
atomic_json(root/'series.json',plan)
windows=json.loads(subprocess.check_output(['pwsh','-NoProfile','-Command',"@{os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber);cpu=(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);nic=(Get-NetAdapter | Select-Object Name,Status,LinkSpeed,HardwareInterface);runtime=(& dotnet --list-runtimes);powershell=$PSVersionTable.PSVersion.ToString()} | ConvertTo-Json -Depth 8"],encoding='utf-8-sig'))
linux=json.loads((archive_windows/'environment-linux.json').read_text())
atomic_json(root/'environment.json',{'controller':windows,'brokerHost':linux})
shutil.copytree(inputs,archive_windows/'controller-inputs',dirs_exist_ok=True)
shutil.copy2(root/'series.json',archive_windows/'series.json')
print('Prepared series:',root)
