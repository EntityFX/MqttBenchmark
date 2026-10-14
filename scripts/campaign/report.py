"""Generate Russian documents exclusively from campaign/experiment artifacts."""
from __future__ import annotations
import base64, csv, html, json, math, pathlib, re, shutil, statistics, zipfile
import xml.etree.ElementTree as ET
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import markdown

BROKERS = ('Aedes', 'Mosquitto', 'ActiveMQ', 'EMQX')
LIMITATIONS = '''Квантили задержки относятся к завершению публикации PUBACK/PUBCOMP; это не задержка доставки подписчику. Для QoS 0 ACK latency неприменима. Натурный RPS использует actualMeasurementSeconds с завершением in-flight операций, модельный RPS — measurement ticks; завершения во время drain входят в модельный числитель. CI95 вычислен по трём повторам (Student-t, df=2), поэтому статистическая мощность ограничена. Хэши обнаруживают изменение сохранённых данных, но не удостоверяют независимую подлинность. CPU/RSS/NIC проверяют установку и не моделируются двойником. Закрепление логического CPU не означает эксклюзивное физическое ядро.'''

def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def atomic_json(path, data):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.partial')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)

def number(value):
    return 'неприменимо' if value is None else f'{value:.6g}'

def table(headers, rows):
    return '\n'.join(['| ' + ' | '.join(headers) + ' |', '| ' + ' | '.join(['---'] * len(headers)) + ' |'] +
                     ['| ' + ' | '.join(str(value).replace('|', '/') for value in row) + ' |' for row in rows])

def save_plot(fig, path):
    fig.tight_layout()
    fig.savefig(path.with_suffix('.svg'))
    fig.savefig(path.with_suffix('.png'), dpi=180)
    plt.close(fig)
    return path.with_suffix('.png').name

def observation_plots(points, assets, broker, stage):
    result = []
    for metric, label in [('completedRps', 'Завершённые публикации, с⁻¹'),
                          ('publishFailureRate', 'Доля отказов публикации'),
                          ('deliveryLossRate', 'Условная доля потерь доставки')]:
        fig, axes = plt.subplots(1, 2, figsize=(11, 4))
        for ax, payload in zip(axes, (16, 256)):
            for qos in range(3):
                rows = sorted((p for p in points if p['messageBytes'] == payload and p['qos'] == qos), key=lambda p: p['publishers'])
                ax.errorbar([p['publishers'] for p in rows], [p[metric]['mean'] for p in rows],
                            yerr=[p[metric]['ci95HalfWidth'] for p in rows], marker='o', capsize=3, label=f'QoS {qos}')
            ax.set(title=f'{payload} байт', xlabel='Publishers N', ylabel=label)
            ax.grid(alpha=.25); ax.legend()
        fig.suptitle(f'{broker}: {stage}; среднее ± CI95')
        result.append(save_plot(fig, assets / f'{broker}-{stage}-{metric}'))
    fig, axes = plt.subplots(1, 2, figsize=(11, 4))
    for ax, payload in zip(axes, (16, 256)):
        for qos in (1, 2):
            rows = sorted((p for p in points if p['messageBytes'] == payload and p['qos'] == qos), key=lambda p: p['publishers'])
            for knot, style in [('p50Ms', '-'), ('p95Ms', '--'), ('p99Ms', ':')]:
                ax.plot([p['publishers'] for p in rows], [p['observedLatencyQuantiles'][knot] for p in rows],
                        style, marker='o', label=f'QoS {qos} {knot[:-2]}')
        ax.set(title=f'{payload} байт', xlabel='Publishers N', ylabel='Завершение публикации, мс')
        ax.grid(alpha=.25); ax.legend(fontsize=8)
    fig.suptitle(f'{broker}: {stage}; QoS 0 ACK latency неприменима')
    result.append(save_plot(fig, assets / f'{broker}-{stage}-latency'))
    return result

def archived_json(attempt, name):
    direct = attempt / name
    if direct.exists():
        return read(direct)
    receipt = attempt / 'archive.json'
    if not receipt.exists():
        return None
    with zipfile.ZipFile(read(receipt)['archivePath']) as archive:
        try:
            return json.loads(archive.read(name))
        except KeyError:
            return None

def telemetry(attempt):
    name = 'stand/telemetry.ndjson'
    direct = attempt / name
    if direct.exists():
        with direct.open(encoding='utf-8-sig') as stream:
            return [json.loads(line) for line in stream if line.strip()]
    if (attempt / 'archive.json').exists():
        with zipfile.ZipFile(read(attempt / 'archive.json')['archivePath']) as archive:
            if name in archive.namelist():
                return [json.loads(line) for line in archive.read(name).decode('utf-8-sig').splitlines() if line.strip()]
    return []

def resource_rows(root, broker):
    rows = []
    plan=read(root/'series.json') if (root/'series.json').exists() else {}
    raw=pathlib.Path(plan.get('rawControllerRoot',str(root/'raw')))
    for campaign in raw.glob(f'{broker}-*'):
        for attempt in campaign.glob('attempts/*/attempt-*'):
            if not (attempt / 'result.json').exists():
                continue
            result = read(attempt / 'result.json')
            guard = archived_json(attempt, 'load-generator-summary.json')
            samples = telemetry(attempt)
            cpu = [s['processCpuPercent'] for s in samples if s.get('processCpuPercent') is not None]
            rss = [s['processRssBytes'] for s in samples if s.get('processRssBytes') is not None]
            rows.append({'campaign': campaign.name, 'key': result['key'], 'attempt': result['attempt'],
                         'status': result['status'], 'failure': result.get('failure'),
                         'measurement': result.get('observation', {}).get('measurement') if result.get('observation') else None,
                         'guard': guard, 'resources': {'captureCpuMeanPercent':statistics.mean(cpu) if cpu else None,
                         'captureRssMaxBytes':max(rss) if rss else None, 'samples':len(samples),
                         'scope':'Full telemetry capture envelope, including warmup/drain/cooldown; process-pinned CPU and broker-process RSS'}})
    return rows

def experiments(root, broker):
    base = root / 'brokers' / broker if broker != 'MqttRelay-Mixed' else root / 'mixed'
    rows = []
    for index in sorted(base.glob('simulations/*/index.json')):
        item = read(index)
        for run in item.get('runs', []):
            summary = read(pathlib.Path(run['directory']) / 'summary.json')
            counters = read(pathlib.Path(run['directory']) / 'counters.json')
            rows.append({**run, 'stage': item['stage'], 'summary': summary,
                         'receivers': counters.get('receivers', {})})
    return rows

def build(root, broker, state):
    root = pathlib.Path(root)
    reports = root / 'reports'; assets = reports / 'assets'
    assets.mkdir(parents=True, exist_ok=True)
    title = broker if broker != 'ActiveMQ' else 'ActiveMQ Classic'
    plan=read(root/'series.json') if (root/'series.json').exists() else {}
    document = [f'# {title}: измерения, двойник и MqttRelay',
                f"Серия: `{root.name}`. Текущее состояние: **{state.get('status', 'подготовка')}**. Завершённый этап: `{state.get('stage', 'нет')}`.",
                '## Установка и воспроизведение',
                'Windows-контроллер 192.168.5.246 → физический Ethernet → Linux-стенд 192.168.5.1. Native managed lifecycle, singleHostSequential, singleCorePinned. Подробные данные архивируются на hd250 после завершения каждой попытки; передача архива не входит в измеряемое окно.',
                '[Манифест серии](../series.json) · [Окружение](../environment.json) · [Реестр архивов](../archive-registry.json)',
                'Продолжение: `python scripts/campaign/run.py --series <абсолютный каталог серии> --resume`. Проверка сохранённых ZIP и повторная агрегация: `python scripts/campaign/run.py --series <каталог> --verify-only`. Точные argv сохранены в журнале команд серии.']
    document.append(f"CPU guard генератора: **{plan.get('cpuGuardPercent',70)}%** (максимальный пересекающий measurement интервал). При высоком CPU наблюдения характеризуют установку с Windows-генератором; насыщение контроллера не является доказательством предела брокера. Raw scratch вне workspace IDE: `{plan.get('rawControllerRoot',str(root/'raw'))}`; после каждой sealed attempt остаются только компактные descriptors.")
    if state.get('error'):
        document += ['## Диагностика', str(state['error'])]
    for stage in ('calibration', 'validation'):
        observation = root / 'brokers' / broker / stage / 'broker-observations.v3.json'
        if not observation.exists():
            continue
        data = read(observation); points = data['points']
        document += [f'## Натурная серия: {stage}',
                     f"Покрытие: **{data['runCount']}/72 успешных ключей**, {len(points)}/24 точек. Исходные повторы и отказы сохраняются в архиве.",
                     f'[Исходные observations](../brokers/{broker}/{stage}/broker-observations.v3.json)',
                     table(['Байт', 'QoS', 'N', 'attempted RPS', 'completed RPS ± CI95', 'failure', 'loss', 'p99, мс', 'RTT, мс'],
                           [[p['messageBytes'], p['qos'], p['publishers'], number(p['attemptedRps']['mean']),
                             number(p['completedRps']['mean']) + ' ± ' + number(p['completedRps']['ci95HalfWidth']),
                             number(p['publishFailureRate']['mean']), number(p['deliveryLossRate']['mean']),
                             number(p['observedLatencyQuantiles']['p99Ms'] if p['observedLatencyQuantiles'] else None), number(p['rttBaselineMs'])] for p in points])]
        for plot in observation_plots(points, assets, broker, stage):
            document.append(f'![{stage}: {plot}](assets/{plot})')
        with (reports / f'{broker}-{stage}.csv').open('w', newline='', encoding='utf-8-sig') as stream:
            writer = csv.writer(stream); writer.writerow(['broker','bytes','qos','N','attemptedRps','completedRps','ci95','failure','loss'])
            for p in points:
                writer.writerow([broker,p['messageBytes'],p['qos'],p['publishers'],p['attemptedRps']['mean'],p['completedRps']['mean'],p['completedRps']['ci95HalfWidth'],p['publishFailureRate']['mean'],p['deliveryLossRate']['mean']])
    resources = resource_rows(root, broker)
    if resources:
        atomic_json(reports / f'{broker}-attempts.json', resources)
        document += ['## Попытки и контроль генератора', f"Всего сохранённых попыток: {len(resources)}, успешных: {sum(r['status']=='success' for r in resources)}.",
                     f'[Результаты попыток и guard](./{broker}-attempts.json)']
        document.append(table(['Ключ','Попытка','Статус','CPU capture mean, %','RSS capture max, MiB','Guard'],
                              [[str(r['key']),r['attempt'],r['status'],number(r['resources']['captureCpuMeanPercent']),
                                number(r['resources']['captureRssMaxBytes']/1024**2 if r['resources']['captureRssMaxBytes'] else None),
                                str(r['guard']['assessment']) if r['guard'] else 'нет итогового отчёта'] for r in resources]))
        document.append('CPU/RSS выше относятся ко всему envelope захвата, включая warmup/drain/cooldown; их нельзя интерпретировать как среднее только измеряемого окна. Guard оценивает каждый пересекающий measurement интервал целиком.')
        measured = [r for r in resources if r['status']=='success' and r['resources']['samples']]
        if measured:
            fig, axes = plt.subplots(1,2,figsize=(11,4))
            for qos in range(3):
                group = [r for r in measured if r['key']['qos']==qos]
                for ax, field, scale, label in [(axes[0],'captureCpuMeanPercent',1,'CPU, %'),(axes[1],'captureRssMaxBytes',1024**2,'RSS, MiB')]:
                    ax.scatter([r['key']['publishers'] for r in group],[r['resources'][field]/scale if r['resources'][field] is not None else float('nan') for r in group],label=f'QoS {qos}',alpha=.6)
                    ax.set(xlabel='Publishers N',ylabel=label); ax.grid(alpha=.2); ax.legend()
            fig.suptitle('Все успешные повторы: CPU mean / RSS max по capture envelope')
            document.append(f'![Ресурсы](assets/{save_plot(fig,assets / (broker+"-resources"))})')
    simulations = experiments(root, broker)
    if simulations:
        atomic_json(reports / f'{broker}-simulations.json', simulations)
        document += ['## Симуляции и критерии',
                     'BrokerFidelity: RPS ≤15%, p99 QoS1/2 ≤20%, абсолютная разница failure/loss ≤0,05. Каждая строка — отдельный seed; notApplicable не является подтверждённым прохождением.',
                     table(['Этап','Байт','QoS','N','Seed','Статус','RPS','Получено receivers','Критерии'],
                           [[r['stage'],r['bytes'],r['qos'],r['publishers'],r['seed'],r['summary']['status'],
                             number(r['summary']['simulated']['completedRps']),sum(r['receivers'].values()),
                             '; '.join(c['name']+': '+c['status'] for c in r['summary']['acceptanceCriteria'])] for r in simulations]),
                     f'[Все summaries, ошибки и counters](./{broker}-simulations.json)']
        fidelity = [r for r in simulations if r['stage'] in ('fidelity','holdout')]
        if fidelity:
            fig, ax = plt.subplots(figsize=(10, max(3, len(fidelity)*.055)))
            metrics = ['rps','publishFailureRate','deliveryLossRate','latencyP99Ms']
            values = [[next((c['error']/c['limit'] if c['error'] is not None and c['limit'] else float('nan')
                              for c in r['summary']['acceptanceCriteria'] if c['name']==name), float('nan')) for name in metrics] for r in fidelity]
            image = ax.imshow(values, aspect='auto', vmin=0, vmax=2, cmap='RdYlGn_r')
            ax.set_xticks(range(4), metrics); ax.set_ylabel('Симуляции в порядке таблицы')
            ax.set_title('Ошибка / порог; >1 означает провал; пусто — notApplicable')
            fig.colorbar(image, ax=ax)
            plot = save_plot(fig, assets / f'{broker}-fidelity-errors')
            document.append(f'![Нормированные ошибки](assets/{plot})')
            fig,axes=plt.subplots(1,2,figsize=(11,4))
            for stage in ('fidelity','holdout'):
                group=[r for r in fidelity if r['stage']==stage and r['summary']['reference']]
                axes[0].scatter([r['summary']['reference']['completedRps'] for r in group],[r['summary']['simulated']['completedRps'] for r in group],label=stage,alpha=.6)
                latency=[r for r in group if r['summary']['reference']['latencyP99Ms'] is not None and r['summary']['simulated']['latencyP99Ms'] is not None]
                axes[1].scatter([r['summary']['reference']['latencyP99Ms'] for r in latency],[r['summary']['simulated']['latencyP99Ms'] for r in latency],label=stage,alpha=.6)
            for ax,label in zip(axes,('RPS','p99, мс')):
                bounds=ax.get_xlim();ax.plot(bounds,bounds,'k--',alpha=.4);ax.set(xlabel='Измерение: '+label,ylabel='Симуляция: '+label);ax.legend();ax.grid(alpha=.2)
            document.append(f'![Измерения и двойник](assets/{save_plot(fig,assets / (broker+"-measured-vs-simulated"))})')
        representative = next((r for r in simulations if r['stage']=='relay' and r['publishers']==1), simulations[0])
        graph_path=pathlib.Path(representative['directory'])/'topology.graphml'
        if graph_path.exists():
            graph=ET.parse(graph_path).getroot(); nodes=[element.attrib['id'] for element in graph.iter() if element.tag.rsplit('}',1)[-1]=='node']
            edges=[element for element in graph.iter() if element.tag.rsplit('}',1)[-1]=='edge']
            if nodes:
                positions={node:(math.cos(2*math.pi*i/len(nodes)),math.sin(2*math.pi*i/len(nodes))) for i,node in enumerate(nodes)}
                fig,ax=plt.subplots(figsize=(10,8))
                for edge in edges:
                    if edge.attrib['source'] in positions and edge.attrib['target'] in positions:
                        a=positions[edge.attrib['source']];b=positions[edge.attrib['target']];ax.annotate('',xy=b,xytext=a,arrowprops={'arrowstyle':'->','alpha':.25,'lw':.6})
                for node,(x,y) in positions.items(): ax.scatter([x],[y],s=60);ax.text(x,y,node,fontsize=6,ha='center',va='bottom')
                ax.axis('off');ax.set_title(f'Фактический GraphML: {len(nodes)} узлов, {len(edges)} связей; N=1')
                document.append(f'![Топология](assets/{save_plot(fig,assets / (broker+"-topology"))})')
            shutil.copy2(graph_path,assets/(broker+'-topology.graphml'))
            document.append(f'[GraphML топологии](assets/{broker}-topology.graphml)')
    document += ['## Интерпретация и ограничения', LIMITATIONS,
                 'Калибровочное воспроизведение и независимый контроль представлены отдельно. Контрольные observations не используются для изменения профиля или транспорта. MqttRelay — модельный эксперимент: натурная точность связанной сети не подтверждена одиночными измерениями брокеров. Reference одиночных брокеров для relay является диагностикой; критерии натурной fidelity неприменимы.',
                 'Сетевые сценарии: четыре брокера, networkLength=2, N publishers на брокер. OfferedRps — суммарная внешняя нагрузка; внутренняя пересылка создаёт дополнительный трафик. Точные ticks/network/config, GraphML и PlantUML сохраняются в каждом эксперименте.']
    text = '\n\n'.join(document) + '\n'
    md = reports / f'{broker}.md'; md.write_text(text, encoding='utf-8')
    body = markdown.markdown(text, extensions=['tables','fenced_code'])
    def inline_image(match):
        path = reports / html.unescape(match.group(1))
        if not path.is_file(): return match.group(0)
        return 'src="data:image/png;base64,' + base64.b64encode(path.read_bytes()).decode() + '"'
    body = re.sub(r'src="([^"]+)"', inline_image, body)
    (reports / f'{broker}.html').write_text('<!doctype html><html lang="ru"><meta charset="utf-8"><title>' + html.escape(title) + '</title><style>body{font:16px/1.5 system-ui;max-width:1200px;margin:30px auto;padding:20px}table{border-collapse:collapse;font-size:13px;display:block;overflow:auto}td,th{border:1px solid #ccc;padding:6px}img{max-width:100%}code{overflow-wrap:anywhere}</style>' + body + '</html>', encoding='utf-8')
