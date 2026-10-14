"""Sequential real campaign, frozen twins, holdout validation and relay networks.

Uses only published CLI commands. Every completed attempt is cold archived before
the next workload. A background disk watchdog interrupts the CLI on low space.
"""
from __future__ import annotations
import argparse, datetime, hashlib, json, os, pathlib, shutil, signal, subprocess, sys, time, uuid, zipfile
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from report import BROKERS, atomic_json, build, read

GIB = 1024**3

def digest(path):
    with pathlib.Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

class Campaign:
    def __init__(self, root):
        self.root = pathlib.Path(root).resolve()
        self.plan = read(self.root / 'series.json')
        self.raw=pathlib.Path(self.plan.get('rawControllerRoot',str(self.root/'raw')))
        self.raw.mkdir(parents=True,exist_ok=True)
        self.storage = pathlib.Path(self.plan['archiveWindowsRoot'])
        self.storage.mkdir(parents=True, exist_ok=True)
        (self.storage / 'attempts').mkdir(exist_ok=True)
        self.state = read(self.root / 'state.json') if (self.root / 'state.json').exists() else {'completed': [], 'brokers': {}}
        self.ssh = [self.plan['sshExecutable'], '-i', self.plan['identityFile'], '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', self.plan['sshTarget']]

    def save(self):
        atomic_json(self.root / 'state.json', self.state)
        atomic_json(self.storage / 'state.json', self.state)

    def remote(self, command):
        return subprocess.check_output(self.ssh + [command], text=True, encoding='utf-8', errors='replace').strip()

    def storage_check(self):
        mount = self.plan['archiveMount']
        actual = self.remote('findmnt -n -o TARGET,SOURCE,FSTYPE -T ' + mount).split()
        if actual != [mount, self.plan['archiveDevice'], 'ext4']:
            raise RuntimeError('hd250 mount identity changed or disk is unavailable; no new workload will start')
        if shutil.disk_usage(self.storage).free < 20*GIB:
            raise RuntimeError('hd250 has less than 20 GiB free')
        if shutil.disk_usage(self.root).free < 10*GIB:
            raise RuntimeError('Windows system disk has less than 10 GiB free')

    def invoke(self, args, tag, cwd=None, allow_fidelity_failure=False):
        self.storage_check()
        log = self.root / 'logs' / (tag + '-' + uuid.uuid4().hex[:8] + '.log')
        log.parent.mkdir(parents=True, exist_ok=True)
        with (self.root / 'commands.ndjson').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps({'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'argv': [str(a) for a in args], 'cwd': str(cwd or self.root), 'log': str(log)}, ensure_ascii=False) + '\n')
        with log.open('w', encoding='utf-8') as output:
            process = subprocess.Popen([str(a) for a in args], cwd=cwd or self.root, stdout=output, stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == 'nt' else 0)
            self.state['active'] = {'tag': tag, 'pid': process.pid, 'log': str(log)}; self.save()
            stopped = None
            while process.poll() is None:
                time.sleep(2)
                try:
                    if shutil.disk_usage(self.root).free < 6*GIB or shutil.disk_usage(self.storage).free < 12*GIB:
                        stopped = 'Low free disk space; workload interrupted before disks fill'
                except OSError:
                    stopped = 'Evidence disk became inaccessible'
                if stopped:
                    process.send_signal(signal.CTRL_BREAK_EVENT if os.name == 'nt' else signal.SIGINT)
                    try: process.wait(timeout=120)
                    except subprocess.TimeoutExpired: process.kill(); process.wait()
                    raise RuntimeError(stopped)
            code = process.returncode
        self.state.pop('active', None); self.save()
        if code and not (allow_fidelity_failure and code == 3):
            raise RuntimeError(f'{tag}: CLI exit {code}; evidence preserved, diagnostic: {log}\n' + log.read_text(encoding='utf-8', errors='replace')[-3000:])
        return code

    def common(self, broker, stage):
        inputs = self.root / 'inputs'
        return ['--config', inputs / f'{broker}-{stage}.json', '--stand', inputs / f'{broker}-stand.json',
                '--benchmark-repo', self.plan['benchmarkRepository'], '--stand-script', pathlib.Path(self.plan['benchmarkRepository']) / 'scripts/BrokerStand.ps1']

    def once(self, key, action):
        if key in self.state['completed']: return
        action()
        self.state['completed'].append(key); self.save()

    def report(self, broker, stage, status='в работе', error=None):
        entry = {'stage': stage, 'status': status}
        if error: entry['error'] = error
        self.state['brokers'][broker] = entry; self.save()
        build(self.root, broker, entry)
        shutil.copytree(self.root / 'reports', self.storage / 'reports', dirs_exist_ok=True)
        for name in ('inputs','brokers','mixed','preflight'):
            if (self.root/name).exists(): shutil.copytree(self.root/name,self.storage/'controller-bundle'/name,dirs_exist_ok=True)
        for name in ('series.json','environment.json','state.json','transport.json','commands.ndjson','archive-registry.json'):
            if (self.root/name).exists():
                (self.storage/'controller-bundle').mkdir(exist_ok=True)
                shutil.copy2(self.root/name,self.storage/'controller-bundle'/name)
        # Only compact attempt descriptors are needed to resume; every large file
        # is in a verified ZIP on the disk. Unsealed evidence remains local.
        for path in self.raw.glob('*/**/*.json'):
            if path.name not in ('started.json','result.json','sha256.json','archive.json','campaign.json'): continue
            target=self.storage/'controller-bundle'/'raw'/path.relative_to(self.raw)
            target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,target)

    def real(self, broker, stage, pilot=False):
        cli = ['dotnet', self.plan['benchmarkCli']]
        raw = self.raw / f'{broker}-{stage}'
        self.invoke(cli + ['preflight'] + self.common(broker, stage) + ['--output', self.root / 'preflight' / (broker + '-' + stage + '-' + uuid.uuid4().hex[:8])], broker + '-' + stage + '-preflight')
        options = cli + ['matrix'] + self.common(broker, stage) + ['--campaign', raw, '--archive-root', self.storage / 'attempts']
        if (raw / 'campaign.json').exists(): options += ['--resume']
        self.invoke(options, broker + '-' + stage + '-matrix')
        self.registry()
        if not pilot:
            output = self.root / 'brokers' / broker / stage
            if not (output / 'broker-observations.v3.json').exists():
                self.invoke(cli + ['aggregate'] + self.common(broker, stage) + ['--raw', raw, '--output', output], broker + '-' + stage + '-aggregate')
            observed = read(output / 'broker-observations.v3.json')
            if observed['runCount'] != 72 or len(observed['points']) != 24: raise RuntimeError('Incomplete real matrix')
            self.report(broker, stage)
            self.verify_stage(broker, stage)

    def registry(self):
        receipts = []
        for path in sorted(self.raw.glob('*/attempts/*/attempt-*/archive.json')):
            entry = read(path); entry['receipt'] = str(path)
            entry['linuxArchivePath'] = entry['archivePath'].replace(str(self.storage), self.plan['archiveLinuxRoot']).replace('\\', '/')
            receipts.append(entry)
        atomic_json(self.root / 'archive-registry.json', receipts)
        shutil.copy2(self.root / 'archive-registry.json', self.storage / 'archive-registry.json')

    def verify_stage(self, broker, stage):
        raw = self.raw / f'{broker}-{stage}'
        # Restore on hd250, never on a system volume. Read existing ZIPs and check
        # original seals; the CLI then repeats all journal/statistical validation.
        restored = self.storage / 'verification' / (broker + '-' + stage)
        restored = restored.with_name(restored.name + '-' + uuid.uuid4().hex[:8])
        restored.mkdir(parents=True)
        shutil.copy2(raw / 'campaign.json', restored / 'campaign.json')
        for attempt in sorted(raw.glob('attempts/*/attempt-*')):
            receipt = read(attempt / 'archive.json')
            if digest(receipt['archivePath']) != receipt['sha256']: raise RuntimeError('Archive digest mismatch')
            destination = restored / attempt.relative_to(raw)
            destination.mkdir(parents=True)
            with zipfile.ZipFile(receipt['archivePath']) as archive:
                for member in archive.infolist():
                    target = (destination / member.filename).resolve()
                    if not target.is_relative_to(destination.resolve()): raise RuntimeError('Unsafe archive entry')
                archive.extractall(destination)
            seals = read(destination / 'sha256.json')
            for name, sha in seals.items():
                if digest(destination / name) != sha: raise RuntimeError('Restored evidence seal mismatch')
        output = self.storage / 'verification' / (broker + '-' + stage + '-aggregate-' + uuid.uuid4().hex[:8])
        self.invoke(['dotnet', self.plan['benchmarkCli'], 'aggregate'] + self.common(broker, stage) + ['--raw', restored, '--output', output], broker + '-' + stage + '-restore-aggregate')
        original = read(self.root / 'brokers' / broker / stage / 'broker-observations.v3.json')
        repeated = read(output / 'broker-observations.v3.json')
        if original['points'] != repeated['points'] or original['provenance']['identity'] != repeated['provenance']['identity']:
            raise RuntimeError('Recomputed scientific observations differ')
        verification = {'broker': broker, 'stage': stage, 'verified': True, 'originalSha256': digest(self.root / 'brokers' / broker / stage / 'broker-observations.v3.json'), 'restoredAggregateSha256': digest(output / 'broker-observations.v3.json'), 'note': 'Scientific points and campaign identity match; inputSha256 differs between compact and restored representation.'}
        atomic_json(self.root / 'brokers' / broker / stage / 'archive-verification.json', verification)
        # Check mount and resolved scope immediately before deleting only our scratch.
        self.storage_check()
        if not restored.resolve().is_relative_to((self.storage / 'verification').resolve()): raise RuntimeError('Cleanup scope escaped')
        shutil.rmtree(restored)
        shutil.copytree(self.root / 'brokers' / broker / stage, self.storage / 'brokers' / broker / stage, dirs_exist_ok=True)

    def profile(self, broker):
        profile = self.root / 'brokers' / broker / 'broker-calibration.v3.json'
        if not profile.exists():
            self.invoke(['dotnet', self.plan['simulatorCli'], 'calibrate', '--observations', self.root / 'brokers' / broker / 'calibration/broker-observations.v3.json', '--output', profile,
                         '--mqtty-repository', self.plan['mqttYRepository'], '--broker', broker], broker + '-calibrate')
        sha = digest(profile)
        frozen = self.state.setdefault('profiles', {})
        if broker in frozen and frozen[broker] != sha: raise RuntimeError('Frozen twin changed')
        frozen[broker] = sha; self.save()
        return profile

    def simulate(self, broker, stage):
        mixed = broker == 'MqttRelay-Mixed'
        base = self.root / ('mixed' if mixed else 'brokers/' + broker) / 'simulations' / stage
        base.mkdir(parents=True, exist_ok=True)
        index = read(base / 'index.json') if (base / 'index.json').exists() else {'stage': stage, 'runs': []}
        names = list(BROKERS) if mixed else [broker]
        profiles = [self.profile(name) for name in names]
        reference_stage = 'validation' if stage == 'holdout' else 'calibration'
        observations = {name: read(self.root / 'brokers' / name / reference_stage / 'broker-observations.v3.json') for name in names}
        transport = read(self.root / 'transport.json')
        for payload in (16, 256):
            for qos in range(3):
                for publishers in (1, 16, 64, 128):
                    points = {name: next(p for p in observations[name]['points'] if (p['messageBytes'],p['qos'],p['publishers']) == (payload,qos,publishers)) for name in names}
                    relay = stage == 'relay' or mixed
                    offered = (4 if relay else 1) * min(p['attemptedRps']['mean'] for p in points.values())
                    for seed in (42,43,44):
                        key = f'm{payload}.q{qos}.p{publishers}.s{seed}'
                        if any(run['key']==key for run in index['runs']): continue
                        config = read(self.plan['simulatorExample'])
                        options = config['experiment']
                        options.update(topologyMode='mqttRelay' if relay else 'brokerFidelity', brokers=4 if relay else 1,
                                       networkLength=2 if relay else 1, clientsPerBroker=publishers, payloadBytes=payload,
                                       publishQos=['atMostOnce','atLeastOnce','exactlyOnce'][qos], subscribeQos=['atMostOnce','atLeastOnce','exactlyOnce'][qos],
                                       brokerAssignmentMode='mixed' if mixed else broker[0].lower()+broker[1:], mixedBrokerTypes=list(BROKERS) if mixed else [],
                                       offeredRps=offered, randomSeed=seed, parallel=False, refreshStrategy=0, profileClientCountMode='publishers',
                                       warmupTicks=50000, measurementTicks=300000, drainTimeoutTicks=300000)
                        config['network'].update(transport['network'])
                        config['network']['transferTicks'] = transport['transferTicksByBroker'][names[0]] if not mixed else max(transport['transferTicksByBroker'].values())
                        config.update(mqttYRepository=self.plan['mqttYRepository'], mqttBenchmarkRepository=self.plan['benchmarkRepository'])
                        config.pop('brokerProfilePath', None)
                        if mixed: config['brokerProfilePaths'] = [str(p) for p in profiles]
                        else: config['brokerProfilePath'] = str(profiles[0])
                        if stage == 'holdout': config['referenceObservationsPath'] = str(self.root / 'brokers' / broker / 'validation/broker-observations.v3.json')
                        path = base / (key + '.json')
                        if path.exists() and read(path) != config: raise RuntimeError('Simulation config changed')
                        if not path.exists(): atomic_json(path, config)
                        output = self.storage / ('mixed' if mixed else 'brokers/' + broker) / 'simulations' / stage / key
                        # A interrupted/failed run remains; resume creates a new run directory.
                        self.invoke(['dotnet', self.plan['simulatorCli'], 'experiment', '--config', path, '--output', output], broker + '-' + stage + '-' + key,
                                    allow_fidelity_failure=True)
                        directory = max(output.iterdir(), key=lambda p: p.name)
                        summary = read(directory / 'summary.json')
                        if summary['status'] != 'completed': raise RuntimeError('Simulation phase failed')
                        index['runs'].append({'key':key,'directory':str(directory),'bytes':payload,'qos':qos,'publishers':publishers,'seed':seed,'offeredRps':offered})
                        atomic_json(base / 'index.json', index)
                        for name, profile in zip(names, profiles):
                            if digest(profile) != self.state['profiles'][name]: raise RuntimeError('Twin was modified during experiment')
        self.report(broker, stage)

    def freeze_transport(self, broker):
        path = self.root / 'transport.json'
        transport = read(path) if path.exists() else {'network': {'capacityBytesPerSecond':125000000, 'throughputWindowTicks':100, 'queueCapacity':100000, 'networkType':'ethernet'}, 'transferTicksByBroker':{}, 'method':'nearest integer one-way propagation ticks from half mean calibration MQTT echo RTT; tick=0.1ms; includes echo broker overhead; no control-data tuning'}
        if broker not in transport['transferTicksByBroker']:
            points = read(self.root / 'brokers' / broker / 'calibration/broker-observations.v3.json')['points']
            rtt = sum(p['rttBaselineMs'] for p in points)/len(points)
            transport['transferTicksByBroker'][broker] = max(1, round(rtt/(2*.1)))
            atomic_json(path, transport)

    def execute(self):
        for name in (*BROKERS,'MqttRelay-Mixed'):
            if name not in self.state['brokers']: self.report(name,'pending','не начат')
        for broker in BROKERS:
            self.report(broker, 'preparation')
            for stage in ('pilot','calibration','profile','fidelity','validation','holdout','relay'):
                key = broker + '/' + stage
                self.report(broker, stage)
                if stage in ('pilot','calibration','validation'):
                    action = lambda b=broker,s=stage: self.real(b,s,pilot=s=='pilot')
                elif stage == 'profile':
                    def action(b=broker): self.profile(b); self.freeze_transport(b)
                else: action = lambda b=broker,s=stage: self.simulate(b,s)
                self.once(key, action)
            self.report(broker, 'relay', 'завершён')
        self.once('mixed/relay', lambda: self.simulate('MqttRelay-Mixed','relay'))
        self.report('MqttRelay-Mixed','relay','завершён')
        self.state['status']='completed'; self.save()
        shutil.copytree(self.root, self.storage / 'controller-bundle', dirs_exist_ok=True, ignore=shutil.ignore_patterns('raw'))

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--series',required=True); parser.add_argument('--resume',action='store_true'); parser.add_argument('--verify-only',action='store_true')
    args=parser.parse_args(); campaign=Campaign(args.series)
    if not args.resume and campaign.state['completed'] and not args.verify_only: raise RuntimeError('Use --resume for existing campaign')
    try:
        if args.verify_only:
            for broker in BROKERS:
                for stage in ('calibration','validation'):
                    if (campaign.root/'brokers'/broker/stage/'broker-observations.v3.json').exists(): campaign.verify_stage(broker,stage)
        else: campaign.execute()
    except Exception as error:
        campaign.state['status']='technical-failure'; campaign.state['error']=str(error); campaign.save()
        for broker, entry in campaign.state['brokers'].items():
            if entry['status']!='завершён': campaign.report(broker,entry['stage'],'техническая остановка',str(error))
        raise

if __name__=='__main__': main()
