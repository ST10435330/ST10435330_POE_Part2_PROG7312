import {request, byId as $, element, notify, onClick, onForm} from './stream-api.js';
const types = {'Environmental': 'environmental', 'Power Consumption': 'power', 'Actuator': 'actuator'};
let selected = null, skip = 0, busy = false, active = false, version = 0;
let processingPaused = false, simulationRunning = false, sensorPaused = false;
let from = '', to = '', source = null;
const stateKey = 'smartx-stream-state';
function saveState() {
    sessionStorage.setItem(stateKey, JSON.stringify({selected: selected?.id, search: $('streamSearch').value,
        category: $('streamCategory').value, status: $('streamStatus').value, skip, from, to}));
}
async function loadDevices(recordSearch = false) {
    const q = new URLSearchParams({search: $('streamSearch').value, category: $('streamCategory').value,
        status: $('streamStatus').value, skip, take: 30});
    const result = await request('/stream/devices?' + q);
    $('streamDevices').replaceChildren();
    for (const device of result.devices) {
        const button = element('button', device.identifier, device.id === selected?.id ? 'selected' : '');
        button.append(element('small', `${device.category} · ${device.status}`));
        button.onclick = () => choose(device, true).catch(error => notify(error.message, true));
        $('streamDevices').append(button);
    }
    $('streamSearchInfo').textContent = `${result.total} matches · ${result.searchMilliseconds.toFixed(3)} ms`;
    $('streamPrev').disabled = skip === 0; $('streamNext').disabled = skip + 30 >= result.total;
    if (!selected && result.devices.length) await choose(result.devices[0], false);
    else if (recordSearch && result.devices.length === 1) await choose(result.devices[0], true);
    saveState();
}
async function choose(device, observe) {
    selected = device; version++; saveState();
    if (observe) await request('/stream/observations/search', {sensorId: device.id});
    await loadDetail();
    await loadDevices();
}
async function loadDetail() {
    if (!selected) return;
    const id = selected.id, current = version;
    const range = new URLSearchParams({take: 120});
    if (from) range.set('from', new Date(from).toISOString());
    if (to) range.set('to', new Date(to).toISOString());
    const [device, history, health] = await Promise.all([request('/stream/devices/' + id),
        request('/stream/history/' + id + '?' + range), request('/sensors/' + id + '/health')]);
    if (version !== current) return;
    selected = device; sensorPaused = health.paused;
    $('streamTitle').textContent = device.identifier;
    $('streamDetail').textContent = `${device.category} · ${device.location}`;
    $('streamState').textContent = `${device.status} · ${device.timestamp ? new Date(device.timestamp).toLocaleString() : 'Awaiting data'}${device.overrideState === null ? '' : ' · Override: ' + (device.overrideState ? 'ON' : 'OFF')}`;
    $('streamValue').textContent = device.value;
    $('streamCritical').disabled = device.category === 'Actuator';
    $('commandOn').disabled = $('commandOff').disabled = device.category !== 'Actuator';
    $('streamPause').textContent = sensorPaused ? 'Resume simulated device' : 'Pause simulated device';
    $('streamHistoryInfo').textContent = `${history.stored} retained API readings · ${Math.min(30,history.records.length)} table rows shown · ${history.searchMilliseconds.toFixed(3)} ms · ${history.algorithm}`;
    $('streamHistoryRows').replaceChildren();
    for (const log of history.records.slice(-30)) {
        const row = element('tr', '');
        for (const value of [new Date(log.timestamp).toLocaleTimeString(), log.value, log.lane, log.sequence]) row.append(element('td', value));
        if (log.critical) row.className = 'critical-row';
        $('streamHistoryRows').append(row);
    }
    renderChart(history.records);
}
function renderChart(records) {
    const svg = $('streamChart'), ns = 'http://www.w3.org/2000/svg'; svg.replaceChildren();
    const add = (tag, attrs, text) => { const e = document.createElementNS(ns, tag); for (const [k,v] of Object.entries(attrs)) e.setAttribute(k,v); if (text) e.textContent = text; svg.append(e); return e; };
    if (!records.length) { add('text', {x: 20,y: 100,fill: '#555'}, 'Send a reading to see its API timeline.'); return; }
    const values = records.map(r => r.numericValue ?? (r.value.startsWith('ON') ? 1 : 0));
    const times = records.map(r => Date.parse(r.timestamp)), first = times[0], span = Math.max(1, times.at(-1) - first);
    const max = selected.category === 'Environmental' ? 100 : Math.max(selected.category === 'Actuator' ? 1 : 8000, ...values);
    const x = i => 45 + (times[i] - first) / span * 725, y = value => 180 - value / max * 150;
    for (let i = 0; i <= 4; i++) { const value = max * i / 4; add('line', {x1:45,x2:780,y1:y(value),y2:y(value),stroke:'#ddd'}); add('text',{x:2,y:y(value)+4,'font-size':11,fill:'#555'},String(Math.round(value))); }
    add('polyline',{points:values.map((v,i)=>`${x(i)},${y(v)}`).join(' '),fill:'none',stroke:'#386c9d','stroke-width':2});
    records.forEach((r,i) => { const dot=add('circle',{cx:x(i),cy:y(values[i]),r:r.critical?5:3,fill:r.critical?'#b72d26':'#386c9d'}); const title=document.createElementNS(ns,'title');title.textContent=`${r.timestamp}: ${r.value} (${r.lane})`;dot.append(title); });
    add('text',{x:45,y:210,'font-size':11},new Date(first).toLocaleTimeString());
    add('text',{x:680,y:210,'font-size':11},new Date(times.at(-1)).toLocaleTimeString());
}
async function refresh() {
    if (busy || !active) return; busy = true;
    try {
        const [queue, alerts, recent, commands, insights, simulation] = await Promise.all([
            request('/stream/queue'), request('/stream/alerts'), request('/stream/recent'),
            request('/stream/commands'), request('/stream/insights'), request('/stream/simulation')]);
        processingPaused = queue.processingPaused; simulationRunning = simulation.running;
        $('toggleProcessing').textContent = processingPaused ? 'Resume normal processing' : 'Pause normal processing';
        $('toggleSimulation').textContent = simulationRunning ? 'Stop HTTP simulator' : 'Start HTTP simulator';
        $('streamMetrics').replaceChildren();
        for (const [label, value] of [['Normal queued',queue.normalPending],['Critical queued',queue.criticalPending],['API admitted',queue.admitted],['Processed',queue.processed],['Processing rejected',queue.failed],['Unique active errors',alerts.active.length]]) {
            const box = element('div','','metric');box.append(element('b',Number(value).toLocaleString()),element('span',label));$('streamMetrics').append(box);
        }
        renderProcessing(recent); renderCommands(commands); renderInsights(insights); renderAlerts(alerts);
        await loadDevices(); await loadDetail();
        $('streamConnection').textContent = 'API online · live updates';
        $('connection').textContent = 'API online';
    } catch (error) { $('streamConnection').textContent = 'API offline / retrying'; notify(error.message, true); }
    finally { busy = false; }
}
function renderProcessing(logs) {
    $('processingLog').replaceChildren();
    for (const log of logs.slice(-12).reverse()) $('processingLog').append(element('p',`#${log.sequence} ${log.lane} · ${log.identifier}: ${log.value}`,log.critical?'critical-text':''));
    if (!logs.length) $('processingLog').append(element('p','No HTTP telemetry processed yet.'));
}
function renderCommands(commands) { $('undoInfo').textContent = `${commands.undoDepth} commands available to undo${commands.nextUndo ? ' · Next: ' + commands.nextUndo : ''}`; $('undoCommand').disabled = commands.undoDepth === 0; $('commandEntries').replaceChildren(); for (const command of commands.entries.slice(0,8)) $('commandEntries').append(element('p',`${new Date(command.at).toLocaleTimeString()} · ${command.isUndo?'UNDO · ':''}${command.label} · ${command.changes.length} device(s)`)); }
function renderAlerts(alerts) { $('activeAlertInfo').textContent = `${alerts.active.length} unique errors · ${alerts.disconnected} disconnected nodes · ${alerts.transitions} state transitions`; $('activeAlertEvents').replaceChildren(); for(const event of alerts.events.slice(-6).reverse()) $('activeAlertEvents').append(element('p',`${event.identifier}: ${event.code} ${event.active?'raised':'cleared'}`)); }
function renderInsights(insights) { $('streamInsights').replaceChildren(); if(!insights.length)$('streamInsights').append(element('p','No suggested actions at the moment.')); for(const insight of insights){const box=element('div','','insight');box.append(element('strong',insight.identifier),element('p',insight.message),element('small',insight.evidence));if(insight.targetId&&insight.suggestedState!==null){const button=element('button','Review target device');button.onclick=()=>request('/stream/devices/'+insight.targetId).then(d=>choose(d,true)).catch(e=>notify(e.message,true));box.append(button);} $('streamInsights').append(box);} }
onForm('streamFilter',async()=>{skip=0;await loadDevices(true);notify('Device search complete. Select a result to record a search context.');});
onClick('streamPrev',async()=>{skip=Math.max(0,skip-30);await loadDevices();});
onClick('streamNext',async()=>{skip+=30;await loadDevices();});
window.addEventListener('smartx:navigate', async event => {
    active = event.detail === 'stream';
    if (active) { try { if (!source) connect(); await loadPumps(); await refresh(); } catch(error) { notify(error.message,true); } }
});
function connect() {
    source = new EventSource('/api/stream/events');
    source.addEventListener('refresh',()=>refresh());
    source.onerror=()=>{$('streamConnection').textContent='Live stream reconnecting…';};
}
setInterval(()=>refresh(),3000);

function requireSelection() { if (!selected) throw Error('Select a device first.'); return selected; }
async function inspectReceipt(receipt) {
    $('streamReceipt').textContent = JSON.stringify(receipt, null, 2);
    if (!receipt?.id) return;
    // A paused FIFO packet deliberately stays queued. A receipt is not a storage confirmation.
    if (processingPaused && !receipt.critical) { notify('Packet queued. Resume normal processing to store it.'); return; }
    for (let i = 0; i < 30; i++) {
        const result = await request('/stream/receipts/' + receipt.id);
        $('streamReceipt').textContent = JSON.stringify(result, null, 2);
        if (result.status !== 'Queued') { notify(result.status === 'Processed' ? 'Packet processed and stored.' : result.error, result.status === 'Rejected'); return; }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    notify('Still queued. Check the processing counters.');
}
onClick('streamNormal', async () => { const device=requireSelection(); await inspectReceipt(await request('/simulator/'+device.id+'/sample',{spike:false})); await refresh(); });
onClick('streamCritical', async () => { const device=requireSelection(); if(device.category==='Actuator')throw Error('Select a moisture or power sensor.'); await inspectReceipt(await request('/simulator/'+device.id+'/sample',{spike:true})); await refresh(); });
onClick('streamPause', async () => { await request('/simulator/'+requireSelection().id+'/pause',{paused:!sensorPaused}); await loadDetail(); });
onClick('toggleProcessing', async () => { await request('/stream/processing',{running:processingPaused}); await refresh(); });
onClick('toggleSimulation', async () => { await request('/stream/simulation',{running:!simulationRunning,devices:Number($('simulationCount').value)}); await refresh(); });
onForm('streamSend',async()=>{
    const device=requireSelection(), text=$('streamInput').value.trim();
    let value;
    if(device.category==='Actuator') { if(!['true','false'].includes(text))throw Error('Enter true or false.'); value=text==='true'; }
    else { value=Number(text);if(!text||!Number.isFinite(value)||(device.category==='Power Consumption'&&!Number.isInteger(value)))throw Error('Enter the correct numeric type.'); }
    const latest=await request('/stream/devices/'+device.id);
    const time=Math.max(Date.now(),latest.timestamp?Date.parse(latest.timestamp)+1:0);
    const receipt=await request('/telemetry/'+types[device.category],{sensorId:device.id,timestamp:new Date(time).toISOString(),value});
    await inspectReceipt(receipt);await refresh();
});
onClick('streamBatch',async()=>{
    const device=requireSelection(), count=Number($('streamBatchCount').value);
    if(!Number.isInteger(count)||count<1||count>1000)throw Error('Choose 1–1,000 packets.');
    if(simulationRunning)throw Error('Stop the HTTP simulator before a deliberate batch test.');
    const latest=await request('/stream/devices/'+device.id);
    const start=Math.max(Date.now(),latest.timestamp?Date.parse(latest.timestamp)+1:0), batches=[];
    for(let offset=0;offset<count;offset+=64) {
        const batch=[];
        for(let i=offset;i<Math.min(offset+64,count);i++) {
            const value=device.category==='Environmental'?Number((45+Math.random()*10).toFixed(2)):
                device.category==='Power Consumption'?400+Math.floor(Math.random()*200):Math.random()>0.5;
            batch.push({sensorId:device.id,timestamp:new Date(start+i).toISOString(),value});
        }
        batches.push(batch);
    }
    const started=performance.now(), result=await request('/telemetry/'+types[device.category]+'/batch',{batches});
    $('streamReceipt').textContent=JSON.stringify({received:result.received,queued:result.queued,rejected:result.rejected,
        nestedArrayLengths:batches.map(b=>b.length),roundTripMilliseconds:performance.now()-started,errors:result.errors,
        firstReceipt:result.receipts[0],lastReceipt:result.receipts.at(-1)},null,2);
    notify(`${result.queued} packets queued through HTTP; ${result.rejected} rejected at admission. Processing is asynchronous.`);
    await refresh();
});

async function command(state) {
    const device=requireSelection();if(device.category!=='Actuator')throw Error('Select an Actuator device.');
    await request('/stream/commands',{sensorId:device.id,state});notify(`Manual override set to ${state?'ON':'OFF'}.`);await refresh();
}
onClick('commandOn',()=>command(true));onClick('commandOff',()=>command(false));
onClick('undoCommand',async()=>{const entry=await request('/stream/commands/undo',{});notify('Undid: '+entry.label);await refresh();});
onClick('shutdownAll',async()=>{
    if(!confirm('Turn OFF all registered actuators? This is one command and can be undone.'))return;
    const entry=await request('/stream/commands/shutdown-all',{});notify(`${entry.changes.length} actuators switched OFF. Use Undo to restore their previous states.`);await refresh();
});
onForm('linkPump',async()=>{await request('/stream/links',{sensorId:requireSelection().id,actuatorId:$('linkedPump').value});notify('Moisture sensor linked to the selected pump.');await refresh();});
async function loadPumps() {
    const pumps=[];
    for(let offset=0;;offset+=100){const page=await request('/stream/devices?category=Actuator&take=100&skip='+offset);pumps.push(...page.devices);if(offset+100>=page.total)break;}
    const previous=$('linkedPump').value;$('linkedPump').replaceChildren();
    for(const pump of pumps){const option=element('option',pump.identifier);option.value=pump.id;$('linkedPump').append(option);}
    if(pumps.some(p=>p.id===previous))$('linkedPump').value=previous;
}
onForm('streamHistoryFilter',async()=>{from=$('streamFrom').value;to=$('streamTo').value;saveState();await loadDetail();});
onClick('clearStreamRange',async()=>{from=to='';$('streamFrom').value=$('streamTo').value='';saveState();await loadDetail();});
async function initialise() {
    try {
        const saved=JSON.parse(sessionStorage.getItem(stateKey)||'null');
        if(saved){$('streamSearch').value=saved.search||'';$('streamCategory').value=saved.category||'';$('streamStatus').value=saved.status||'';skip=saved.skip||0;from=saved.from||'';to=saved.to||'';$('streamFrom').value=from;$('streamTo').value=to;if(saved.selected)selected=await request('/stream/devices/'+saved.selected);}
        await loadPumps();
    } catch(error){notify(error.message,true);}
    navigate(sessionStorage.getItem('smartx-view')||'landing');
}
initialise();
