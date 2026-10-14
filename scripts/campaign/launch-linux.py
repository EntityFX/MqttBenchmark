"""Start one owned persistent Linux controller service, independently of SSH."""
import argparse, json, os, pathlib, subprocess, sys, uuid
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, read
parser=argparse.ArgumentParser();parser.add_argument('--series',required=True);parser.add_argument('--mode',choices=['preflight','pilot','run','verify'],default='run')
args=parser.parse_args();root=pathlib.Path(args.series).resolve();plan=read(root/'series.json')
if plan.get('controllerMode')!='linuxLocal':raise RuntimeError('Linux controller plan required')
actual=subprocess.check_output(['findmnt','-n','-o','TARGET,SOURCE,FSTYPE','-T',str(root)],text=True).split()
if actual!=[plan['archiveMount'],plan['archiveDevice'],'ext4']:raise RuntimeError('External mount identity mismatch')
if (root/'controller-service.json').exists():
    previous=read(root/'controller-service.json')
    running=subprocess.run(['systemctl','--user','is-active','--quiet',previous['unit']]).returncode==0
    if running:raise RuntimeError('Previous owned controller service is still active')
base=root.parent;temporary=base/'tmp';temporary.mkdir(exist_ok=True)
os.environ.update(TMPDIR=str(temporary),TMP=str(temporary),TEMP=str(temporary),
                  MPLCONFIGDIR=str(base/'matplotlib-cache'),DOTNET_CLI_HOME=str(base/'dotnet-home'),
                  DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1',
                  MQB_STAND_SHELL=str(pathlib.Path(plan['archiveMount'])/'mqtty-evidence/tools/pwsh/pwsh'))
unit='mqttbenchmark-controller-'+uuid.uuid4().hex+'.service'
argv=[str(pathlib.Path(__file__).with_name('run.py')),'--series',str(root),'--resume']
flag={'preflight':'--preflight-only','pilot':'--pilot-only','verify':'--verify-only'}.get(args.mode)
if flag:argv.append(flag)
cpus=[]
for part in plan['controllerCpuSet'].split(','):
    ends=part.split('-');cpus.extend(range(int(ends[0]),int(ends[-1])+1))
payload={'unit':unit,'description':'MqttBenchmark owned Linux controller: '+args.mode,
         'workingDirectory':str(root),'executable':sys.executable,'args':argv,
         'stdout':str(root/(args.mode+'-controller.log')),'stderr':str(root/(args.mode+'-controller.err.log')),
         'memoryLimit':plan['controllerMemoryMaxBytes'],'cpus':cpus}
subprocess.run(['/bin/python3',str(pathlib.Path(plan['benchmarkRepository'])/'scripts/LaunchNativeSystemd.py')],input=json.dumps(payload),text=True,check=True)
atomic_json(root/'controller-service.json',{'unit':unit,'mode':args.mode,'argv':[sys.executable]+argv,'cpuSet':plan['controllerCpuSet'],'memoryMaxBytes':plan['controllerMemoryMaxBytes']})
print(unit)
