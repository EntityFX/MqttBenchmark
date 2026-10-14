import json, pathlib, sys, tempfile, unittest
from types import SimpleNamespace
from unittest import mock
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, build, read
from run import Campaign

class Reports(unittest.TestCase):
    def test_linux_report_identifies_shared_host_and_capacity_assumption(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory)
            atomic_json(root/'series.json',{'controllerMode':'linuxLocal','seriesId':'linux-series','cpuGuardPercent':99})
            build(root,'Aedes',{'status':'не начат','stage':'pending'})
            text=(root/'reports/Aedes.md').read_text(encoding='utf-8')
            self.assertIn('linux-series',text)
            self.assertIn('MQTT через 127.0.0.1',text)
            self.assertIn('не измерение loopback',text)
            self.assertNotIn('Windows-контроллер 192.168.5.246 →',text)

    def test_linux_guard_checks_system_partition_separately_from_external_raw(self):
        campaign=Campaign.__new__(Campaign);campaign.root=pathlib.Path('/external/controller');campaign.storage=pathlib.Path('/external/evidence')
        campaign.plan={'archiveMount':'/external','archiveDevice':'/dev/sdc1','systemDiskRoot':'/'}
        campaign.remote=lambda command:'/external /dev/sdc1 ext4'
        usage=lambda path:SimpleNamespace(free=(5 if pathlib.Path(path)==pathlib.Path('/') else 100)*1024**3)
        with mock.patch('run.shutil.disk_usage',side_effect=usage):
            with self.assertRaisesRegex(RuntimeError,'system disk'):campaign.storage_check()

    def test_backup_failure_preserves_local_supervision_state(self):
        with tempfile.TemporaryDirectory() as directory:
            campaign=Campaign.__new__(Campaign);campaign.root=pathlib.Path(directory);campaign.storage=campaign.root/'offline'
            campaign.state={'active':{'pid':123,'tag':'synthetic'}}
            real_write=atomic_json
            def write(path,data):
                if pathlib.Path(path).parent==campaign.storage:raise OSError('disk disappeared')
                real_write(path,data)
            with mock.patch('run.atomic_json',side_effect=write):campaign.save()
            self.assertEqual(123,read(campaign.root/'state.json')['active']['pid'])
            self.assertIn('disk disappeared',campaign.state['backupError'])

    def test_missing_mount_prevents_starting_any_cli(self):
        campaign=Campaign.__new__(Campaign)
        campaign.plan={'archiveMount':'/run/media/entityfx/hd250','archiveDevice':'/dev/sdc1'}
        campaign.remote=lambda command:'/ /dev/sda2 ext4'
        with mock.patch('run.subprocess.Popen') as process:
            with self.assertRaisesRegex(RuntimeError,'mount identity'):campaign.invoke(['synthetic'],'test')
            process.assert_not_called()

    def test_pending_report_is_honest_and_success_plot_values_come_from_observations(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory)
            build(root,'Aedes',{'status':'не начат','stage':'pending'})
            self.assertIn('не начат',(root/'reports/Aedes.md').read_text(encoding='utf-8'))
            points=[]
            for payload in (16,256):
                for qos in (0,1,2):
                    for n in (1,16,64,128):
                        stats=lambda mean:{'mean':mean,'ci95HalfWidth':.1,'count':3}
                        points.append({'messageBytes':payload,'qos':qos,'publishers':n,'attemptedRps':stats(20),'completedRps':stats(12.5),
                                       'publishFailureRate':stats(.2),'deliveryLossRate':stats(.1),'rttBaselineMs':.25,
                                       'observedLatencyQuantiles':None if qos==0 else {'p50Ms':1,'p95Ms':2,'p99Ms':3}})
            atomic_json(root/'brokers/Aedes/calibration/broker-observations.v3.json',{'runCount':72,'points':points})
            build(root,'Aedes',{'status':'в работе','stage':'calibration'})
            text=(root/'reports/Aedes.md').read_text(encoding='utf-8')
            html=(root/'reports/Aedes.html').read_text(encoding='utf-8')
            self.assertIn('12.5 ± 0.1',text)
            self.assertIn('data:image/png;base64,',html)
            self.assertNotIn('src="assets/',html)
            self.assertIn('неприменима',text)
            self.assertEqual(25,len((root/'reports/Aedes-calibration.csv').read_text(encoding='utf-8-sig').splitlines()))

if __name__=='__main__':unittest.main()
