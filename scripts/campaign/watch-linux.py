"""Mirror compact Linux campaign documents to the Windows artifact directory."""
import argparse, datetime, pathlib, shutil, sys, time
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
from report import atomic_json, read
parser=argparse.ArgumentParser();parser.add_argument('--source',required=True);parser.add_argument('--destination',required=True);parser.add_argument('--once',action='store_true')
args=parser.parse_args();source=pathlib.Path(args.source).resolve();destination=pathlib.Path(args.destination).resolve()
if source==destination or destination.is_relative_to(source):raise RuntimeError('Mirror destination must be separate')
destination.mkdir(parents=True,exist_ok=True)
while True:
    status='waiting'
    try:
        for name in ('reports','inputs','brokers','mixed'):
            if (source/name).exists():shutil.copytree(source/name,destination/name,dirs_exist_ok=True)
        for name in ('series.json','environment.json','state.json','transport.json','commands.ndjson','archive-registry.json'):
            if (source/name).exists():shutil.copy2(source/name,destination/name)
        if (source/'state.json').exists():status=read(source/'state.json').get('status','running')
        atomic_json(destination/'mirror-status.json',{'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'status':status,'source':str(source)})
    except OSError as error:
        atomic_json(destination/'mirror-status.json',{'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'status':'connection-error','error':str(error),'source':str(source)})
    if args.once or status in ('completed','technical-failure'):break
    time.sleep(30)
