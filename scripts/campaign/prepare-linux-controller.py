"""Create a separate same-host campaign after a two-host technical rejection."""
import argparse, datetime, hashlib, json, os, pathlib, shutil, subprocess, sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, BROKERS, read

parser=argparse.ArgumentParser()
parser.add_argument('--series',required=True)
parser.add_argument('--repository',required=True)
parser.add_argument('--mqtty-repository',required=True)
parser.add_argument('--cpu-threshold-percent',type=float,default=99)
parser.add_argument('--fallback-evidence',required=True)
args=parser.parse_args()
if not 0<args.cpu_threshold_percent<=100:raise ValueError('Invalid CPU threshold')
base=pathlib.Path(args.series).resolve();repo=pathlib.Path(args.repository).resolve();mqtty=pathlib.Path(args.mqtty_repository).resolve()
root=base/'controller';storage=base/'evidence';tools=pathlib.Path('/run/media/entityfx/hd250/mqtty-evidence/tools')
if root.exists():raise RuntimeError('A fresh series is required; existing inputs cannot be overwritten')
subprocess.run([sys.executable,str(pathlib.Path(__file__).with_name('prepare-linux.py')),'--series',str(base),'--repository',str(repo),'--loopback'],check=True)
root.mkdir();storage.mkdir();(storage/'attempts').mkdir();(root/'inputs').mkdir()
for broker in BROKERS:
    stand=read(base/'inputs'/f'{broker}-stand.json')
    atomic_json(root/'inputs'/f'{broker}-stand.json',stand)
    campaign=read(base/'inputs'/f'{broker}-calibration.json');campaign['cpuThresholdPercent']=args.cpu_threshold_percent
    atomic_json(root/'inputs'/f'{broker}-calibration.json',campaign)
    campaign['seed']=20260910;atomic_json(root/'inputs'/f'{broker}-validation.json',campaign)
    campaign.update(seed=20260908,messageBytes=[16],qos=[0,1,2],publishers=[128],repeats=1)
    # Full measurement duration catches load/coverage failures missed by short pilots.
    atomic_json(root/'inputs'/f'{broker}-pilot.json',campaign)
plan={'schemaVersion':1,'seriesId':base.name,'controllerMode':'linuxLocal',
      'createdAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
      'benchmarkRepository':str(repo),'mqttYRepository':str(mqtty),
      'benchmarkCli':str(tools/'controller/bin/EntityFX.MqttBenchmark.Cli.dll'),
      'simulatorCli':str(tools/'simulator/EntityFX.MqttY.MqttRelay.App.dll'),
      'simulatorExample':str(mqtty/'doc/examples/broker-fidelity.v3.json'),
      'archiveStorageRoot':str(storage),'archiveLinuxRoot':str(storage),
      'archiveMount':'/run/media/entityfx/hd250','archiveDevice':'/dev/sdc1','systemDiskRoot':'/',
      'rawControllerRoot':str(base/'raw'),'cpuGuardPercent':args.cpu_threshold_percent,
      'controllerCpuSet':'1-11,13-23','brokerCpuSet':'0','excludedControllerSmtSibling':'12',
      'controllerMemoryMaxBytes':8*1024**3,'brokerMemoryMaxBytes':4*1024**3,
      'loadGeneratorSampler':'separate .NET process; shared OS monotonic clock; strict maximum interval 1.25s',
      'fallbackEvidence':args.fallback_evidence,'brokerOrder':list(BROKERS),
      'expectedRealRuns':576,'expectedFidelityRuns':576,'expectedRelayRuns':360,
      'modelCapacityNote':'1 Gbit/s Ethernet is a declared simulation assumption; native loopback capacity is not measured'}
atomic_json(root/'series.json',plan)
linux=read(base/'environment-linux.json')
hashes={}
for directory in (tools/'controller/bin',tools/'simulator'):
    if not directory.exists():raise RuntimeError('Published CLI missing: '+str(directory))
    for binary in sorted(directory.rglob('*')):
        if binary.is_file():
            with binary.open('rb') as stream:hashes[str(binary)]=hashlib.file_digest(stream,'sha256').hexdigest()
atomic_json(root/'environment.json',{'controller':linux,'brokerHost':linux,'sameHost':True,
                                  'captureScope':'Preparation snapshot; runtime affinity is verified per process',
                                  'controllerCpuSet':plan['controllerCpuSet'],'brokerCpuSet':'0','publishedCliSha256':hashes,
                                  'reportPackages':subprocess.check_output([sys.executable,'-m','pip','freeze'],text=True)})
print(str(root))
