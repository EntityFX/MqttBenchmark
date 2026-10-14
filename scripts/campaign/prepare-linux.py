"""Prepare isolated broker copies and pinned native inputs on the evidence disk."""
import argparse, hashlib, json, pathlib, shutil, subprocess

def capture(args):
    result=subprocess.run(args,text=True,capture_output=True)
    return {'argv':args,'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr}

parser=argparse.ArgumentParser();parser.add_argument('--series',required=True);parser.add_argument('--repository',required=True)
args=parser.parse_args();root=pathlib.Path(args.series).resolve();repo=pathlib.Path(args.repository).resolve()
mount=pathlib.Path('/run/media/entityfx/hd250')
if not root.is_relative_to(mount/'mqtty-evidence') or subprocess.check_output(['findmnt','-n','-o','TARGET','-T',str(root.parent)],text=True).strip()!=str(mount):
    raise RuntimeError('Evidence root must be on mounted hd250')
root.mkdir(exist_ok=True);tools=mount/'mqtty-evidence/tools';brokers=root/'brokers-native'
brokers.mkdir(exist_ok=True);(root/'worker').mkdir(exist_ok=True);(root/'inputs').mkdir(exist_ok=True)
for name in ('aedes','activemq','emqx'):
    target=brokers/name
    if not target.exists():shutil.copytree(tools/name,target,ignore=shutil.ignore_patterns('data','log','logs'))
shutil.copy2(repo/'docker/brokers/aedes/server.js',brokers/'aedes/server.js')
classic=brokers/'activemq/apache-activemq-6.3.2'
xml=classic/'conf/activemq.xml';text=xml.read_text();text=text.replace('mqtt://127.0.0.1:3883','mqtt://192.168.5.1:3883');xml.write_text(text)
if 'persistent="false"' not in text:raise RuntimeError('ActiveMQ must have persistence disabled before measurement')
emqx=brokers/'emqx';conf=emqx/'etc/emqx.conf';conf.write_text(conf.read_text().replace('127.0.0.1:4883','192.168.5.1:4883'))
(emqx/'data').mkdir(exist_ok=True)
mosq=brokers/'mosquitto.conf';mosq.write_text('listener 2883 192.168.5.1\nallow_anonymous true\npersistence false\nlog_dest none\nsys_interval 2\n')
java=pathlib.Path('/usr/bin/java').resolve();beam=next(emqx.glob('erts-*/bin/beam.smp'))
specs=[
 {'name':'Aedes','mqttUri':'mqtt://192.168.5.1:1883','executable':'/usr/bin/node','args':[str(brokers/'aedes/server.js'),'1883'],'configFiles':[str(brokers/'aedes/server.js'),str(brokers/'aedes/package-lock.json')],'versionArgs':['--version'],'brokerVersion':'1.1.2'},
 {'name':'Mosquitto','mqttUri':'mqtt://192.168.5.1:2883','executable':'/usr/sbin/mosquitto','args':['-c',str(mosq)],'configFiles':[str(mosq)],'versionArgs':['-h'],'brokerVersion':'2.0.15'},
 {'name':'ActiveMQ','mqttUri':'mqtt://192.168.5.1:3883','executable':str(java),'launcherExecutable':str(classic/'bin/activemq'),'args':['console'],'workingDirectory':str(classic),'configFiles':[str(xml),str(classic/'bin/env')],'versionArgs':['-version'],'brokerVersion':'6.3.2','environment':{'JAVA_HOME':str(java.parent.parent)}},
 {'name':'EMQX','mqttUri':'mqtt://192.168.5.1:4883','executable':str(beam),'launcherExecutable':str(emqx/'bin/emqx'),'args':['foreground'],'workingDirectory':str(emqx),'configFiles':[str(conf),str(emqx/'etc/base.hocon'),str(emqx/'etc/vm.args')],'versionArgs':[],'brokerVersion':'6.3.0','startTimeoutSeconds':60,'environment':{'DEBUG':'0'}}]
if not (classic/'bin/env').exists():specs[2]['configFiles'].remove(str(classic/'bin/env'))
snapshot={'role':'Linux broker host','captures':[capture(command) for command in
 [['uname','-a'],['cat','/etc/os-release'],['lscpu'],['free','-h'],['df','-h'],['ip','-4','address'],['ip','route'],['timedatectl','status'],['ps','-eo','pid,comm,pcpu,pmem'],['node','--version'],[str(java),'-version'],['/usr/sbin/mosquitto','-h'],[str(tools/'pwsh/pwsh'),'-NoProfile','-Command','$PSVersionTable.PSVersion.ToString()']]],
 'governor':pathlib.Path('/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor').read_text().strip(),
 'smtSiblings':pathlib.Path('/sys/devices/system/cpu/cpu0/topology/thread_siblings_list').read_text().strip(),
 'nicMbps':int(pathlib.Path('/sys/class/net/enp6s0/speed').read_text()),'mount':capture(['findmnt','-T',str(root)])}
if snapshot['nicMbps']!=1000:raise RuntimeError('Transport defaults require observed 1 Gbit/s data link')
hashes={}
for target in brokers.rglob('*'):
    if target.is_file():
        with target.open('rb') as stream:hashes[str(target.relative_to(brokers))]=hashlib.file_digest(stream,'sha256').hexdigest()
snapshot['distributionSha256']=hashes
(root/'environment-linux.json').write_text(json.dumps(snapshot,ensure_ascii=False,indent=2))
for spec in specs:
    name=spec['name'];sp=root/'inputs'/f'{name}-spec.json';sp.write_text(json.dumps({'brokers':[spec]},indent=2))
    subprocess.run([str(tools/'pwsh/pwsh'),'-NoProfile','-File',str(repo/'scripts/NewNativeStand.ps1'),'-BrokerSpecPath',str(sp),'-StandPath',str(root/'inputs'/f'{name}-stand.json'),'-CampaignPath',str(root/'inputs'/f'{name}-calibration.json'),'-CpuMode','singleCorePinned','-CpuSet','0','-Lifecycle','managed'],check=True)
print('Isolated Linux broker inputs prepared on hd250.')
