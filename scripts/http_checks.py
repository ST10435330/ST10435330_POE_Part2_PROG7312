"""Part 1 + Part 2 HTTP checks. Build Release first; Python 3 uses only its standard library."""
import json, os, shutil, subprocess, tempfile, time
import urllib.request, urllib.error
from pathlib import Path
from datetime import datetime, timezone, timedelta
root = Path(__file__).resolve().parents[1]
dotnet = os.environ.get('SMARTX_DOTNET', 'dotnet')
base = 'http://127.0.0.1:5089'
data = tempfile.mkdtemp(prefix='smartx-http-')
log = open(os.path.join(data, 'server.log'), 'w')
process = subprocess.Popen([dotnet, 'run', '--project', str(root/'src/SmartX.Api'), '-c', 'Release', '--no-build', '--urls', base], env=dict(os.environ, DataDirectory=data), stdout=log, stderr=log)
checks = 0
def request(path, body=None, expected=200, raw=None, content_type=None):
    payload = raw if raw is not None else json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base+path, data=payload, headers={'Content-Type': content_type or 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=30) as response: status, out = response.status, response.read()
    except urllib.error.HTTPError as error: status, out = error.code, error.read()
    assert status == expected, (path, status, out[:500])
    try: return json.loads(out)
    except ValueError: return out

def check(condition, name):
    global checks
    assert condition, name
    checks += 1
    print('PASS HTTP:', name)
def stamp(offset=0): return (datetime.now(timezone.utc)+timedelta(seconds=offset)).isoformat()
def packet(sensor, value, offset=0): return {'sensorId': sensor['id'], 'timestamp': stamp(offset), 'value': value}
def send(kind, payload): return request('/api/telemetry/'+kind, payload, 202)
def wait_receipt(receipt):
    for _ in range(100):
        result = request('/api/stream/receipts/'+receipt['id'])
        if result['status'] != 'Queued':
            assert result['status'] == 'Processed', result
            return result
        time.sleep(.05)
    raise AssertionError('Receipt never completed: '+str(receipt))
def register(name, category='Environmental', interval=3600):
    return request('/api/sensors', {'identifier': name, 'category': category, 'path': ['Facility A', 'Zone 1', 'Sub-Zone B'], 'expectedIntervalSeconds': interval}, 201)
try:
    for _ in range(150):
        try: request('/api/health'); break
        except (OSError, AssertionError): time.sleep(.1)
    else: raise RuntimeError('Server did not start')
    check(b'SMART-X' in request('/'), 'client landing is served')
    check(request('/api/summary')['accepted'] == 120000, '120000 startup seed records are separate from live admissions')
    for filename in ['navigation.js', 'stream.js', 'stream-api.js', 'styles.css']:
        check(len(request('/'+filename)) > 100, filename+' is served')
    moisture, power, actuator = request('/api/sensors?take=3')
    for sensor, kind, value in [(moisture,'environmental',55.5),(power,'power',700),(actuator,'actuator',False)]:
        receipt = send(kind, packet(sensor, value)); wait_receipt(receipt)
        check(request('/api/telemetry/'+kind+'/'+sensor['id'])[-1]['value'] == value, kind+' POST is processed and stored')
    request('/api/telemetry/power',packet(power,5.5),400)
    request('/api/telemetry/actuator',packet(actuator,'false'),400)
    request('/api/telemetry/actuator',{'sensorId':actuator['id'],'timestamp':stamp()},400)
    request('/api/telemetry/environmental',packet(moisture,101),400)
    check(True,'wrong JSON types, missing values and invalid physical ranges are rejected')
    request('/api/sensors',{'identifier':'BAD-ZONE','category':'Actuator','path':['Facility A','Maintenance'],'expectedIntervalSeconds':1},400)
    check(True,'recursive disabled deployment is rejected')
    sensor = register('HTTP-FIFO')
    request('/api/stream/processing',{'running':False})
    first = send('environmental',packet(sensor,50,-3))
    second = send('environmental',packet(sensor,51,-2))
    urgent = send('environmental',packet(sensor,5,-1))
    wait_receipt(urgent)
    check(request('/api/stream/receipts/'+first['id'])['status']=='Queued','critical packet bypasses the paused FIFO queue')
    request('/api/stream/processing',{'running':True})
    wait_receipt(first);wait_receipt(second)
    history = request('/api/stream/history/'+sensor['id'])['records']
    check([r['value'] for r in history]==['50.0 %','51.0 %','5.0 %'],'timeline is timestamp-sorted despite priority processing')
    logs = [r for r in request('/api/stream/recent') if r['sensorId']==sensor['id']]
    check([r['value'] for r in logs]==['5.0 %','50.0 %','51.0 %'],'processing order proves priority then FIFO')
    request('/api/telemetry/environmental',{'sensorId':sensor['id'],'timestamp':history[0]['timestamp'],'value':50},409)
    check(True,'stored timestamp replay rejected')
    batch = register('HTTP-BATCH')
    rows = [packet(batch,50+i*.1,-1+i*.01) for i in range(5)]
    rows[2]['value'] = 105
    result = request('/api/telemetry/environmental/batch',{'batches':[rows[:3],rows[3:]]})
    for receipt in result['receipts']: wait_receipt(receipt)
    check(result['queued']==4 and result['rejected']==1 and len(request('/api/stream/history/'+batch['id'])['records'])==4,'jagged HTTP batch partially accepts valid records')
    request('/api/telemetry/environmental/batch',{'batches':[]},400)
    request('/api/stream/commands',{'sensorId':actuator['id']},400)
    request('/api/stream/commands',{'sensorId':moisture['id'],'state':True},400)
    check(True,'invalid batch and malformed or wrong-device commands rejected')
    pump = register('HTTP-PUMP','Actuator')
    wait_receipt(send('actuator',packet(pump,False)))
    request('/api/stream/commands',{'sensorId':pump['id'],'state':True})
    check(request('/api/stream/devices/'+pump['id'])['value']=='ON / OPEN','override changes actual simulated state')
    request('/api/stream/commands/undo',{})
    check(request('/api/stream/devices/'+pump['id'])['value']=='OFF / CLOSED','undo restores previous state')
    request('/api/stream/links',{'sensorId':sensor['id'],'actuatorId':pump['id']})
    check(any(i['key']=='linked:'+sensor['id'] for i in request('/api/stream/insights')),'low moisture plus pump link generates suggested action')
    for _ in range(3):
        request('/api/stream/observations/search',{'sensorId':sensor['id']})
        request('/api/stream/commands',{'sensorId':pump['id'],'state':True})
        request('/api/stream/commands',{'sensorId':pump['id'],'state':False})
    check(any(i['key']=='pattern:'+sensor['id'] and i['support']==3 for i in request('/api/stream/insights')),'three repeated search-command pairs generate learned suggestion')
    fast = register('HTTP-DISCONNECT',interval=1)
    wait_receipt(send('environmental',packet(fast,50)))
    time.sleep(4.2)
    active = request('/api/stream/alerts')['active']
    check(sum(a['sensorId']==fast['id'] and a['code']=='Disconnected' for a in active)==1,'disconnect alert is unique across repeated checks')
    wait_receipt(send('environmental',packet(fast,50)))
    time.sleep(1.2)
    check(not any(a['sensorId']==fast['id'] for a in request('/api/stream/alerts')['active']),'fresh packet clears disconnect')
    boundary='SmartXBoundary'
    payload=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="device.json"\r\nContent-Type: application/json\r\n\r\n{{"interval":5}}\r\n--{boundary}--\r\n').encode()
    attachment=request('/api/sensors/'+sensor['id']+'/attachments',raw=payload,content_type='multipart/form-data; boundary='+boundary)
    check(request('/api/sensors/'+sensor['id']+'/attachments/'+attachment['id'])=={'interval':5},'Part 1 multipart attachment upload and download still work')
    count=request('/api/stream/queue')['admitted']
    request('/api/stream/simulation',{'running':True,'devices':3})
    time.sleep(1.5)
    request('/api/stream/simulation',{'running':False,'devices':3})
    check(request('/api/stream/queue')['admitted']>=count+3,'background device simulator hits HTTP ingestion queues')
    load=request('/api/simulator/load',{'rounds':2})
    check(load['packets']==6 and 'HTTP' in load['scope'],'load exercise also uses HTTP')
    with urllib.request.urlopen(base+'/api/stream/events',timeout=5) as response:
        check(response.readline().strip()==b'event: refresh' and b'normalPending' in response.readline(),'server-sent events publish live queue state')
    print(f'PASS: {checks} HTTP checks.')
except Exception:
    log.flush()
    print(Path(log.name).read_text()[-5000:])
    raise
finally:
    process.terminate()
    try:process.wait(timeout=10)
    except subprocess.TimeoutExpired:process.kill();process.wait()
    log.close();shutil.rmtree(data)
