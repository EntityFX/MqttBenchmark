import json, pathlib, sys, tempfile, unittest
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, build, read

class Reports(unittest.TestCase):
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
