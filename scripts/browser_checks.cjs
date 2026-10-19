/* Optional: SMARTX_DOTNET=/path/to/dotnet SMARTX_PLAYWRIGHT_MODULE=/absolute/path/to/playwright
   SMARTX_BROWSER_EXECUTABLE=/optional/path/to/chrome node scripts/browser_checks.cjs
   Install Playwright and its Chromium browser in a separate tools directory first. */
const {chromium} = require(process.env.SMARTX_PLAYWRIGHT_MODULE || 'playwright');
const {spawn} = require('node:child_process');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path'), assert = require('node:assert/strict');
const root = path.resolve(__dirname,'..'), directory = fs.mkdtempSync(path.join(os.tmpdir(),'smartx-browser-'));
const base = 'http://127.0.0.1:5090';
const processApi = spawn(process.env.SMARTX_DOTNET || 'dotnet',['run','--project',path.join(root,'src/SmartX.Api'),'-c','Release','--no-build','--urls',base],{env:{...process.env,DataDirectory:directory},stdio:'ignore'});
const delay = ms=>new Promise(resolve=>setTimeout(resolve,ms));
let browser;
(async()=>{
    for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)break;}catch{}await delay(100);}
    browser = await chromium.launch({headless:true,executablePath:process.env.SMARTX_BROWSER_EXECUTABLE || undefined,args:['--no-sandbox']});
    const page = await browser.newPage({viewport:{width:1440,height:1000}});
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    async function wait(text,id='streamMessage'){await page.waitForFunction(({id,text})=>document.getElementById(id).textContent.includes(text),{id,text});}
    await page.goto(base);await page.locator('#enterStream').click();
    await page.locator('#streamSearch').fill('ESP32-0000');await page.locator('#streamFilter button').click();await wait('ESP32-0000','streamTitle');
    await page.locator('#toggleProcessing').click();await wait('Resume','toggleProcessing');
    await page.locator('#stream details').filter({has:page.locator('#streamSend')}).locator('summary').click();
    await page.locator('#streamBatchCount').fill('8');await page.locator('#streamBatch').click();await wait('8 packets queued');
    await page.locator('#streamCritical').click();await wait('processed and stored');await wait('5.0 %','streamValue');
    let q=await (await fetch(base+'/api/stream/queue')).json();assert.equal(q.normalPending,8);assert.equal(q.processed,1);
    await page.locator('#toggleProcessing').click();await wait('Pause','toggleProcessing');
    await page.waitForFunction(async()=>{const q=await(await fetch('/api/stream/queue')).json();return q.normalPending===0&&q.processed===9;});
    console.log('PASS browser: single/batch HTTP, priority bypass, normal resume and live graph');
    await page.locator('[data-view="dashboard"]').click();await page.locator('#dashboard').waitFor({state:'visible'});
    await page.locator('#register').evaluate(form=>form.closest('details').open=true);
    await page.locator('#register [name="identifier"]').fill('BROWSER-PUMP');await page.locator('#register [name="category"]').selectOption('Actuator');
    await page.locator('#register [name="interval"]').fill('3600');await page.locator('#register button').click();await wait('Sensor registered','message');
    await page.locator('[data-view="stream"]').click();await page.locator('#stream').waitFor({state:'visible'});await wait('ESP32-0000','streamTitle');
    assert.equal(await page.locator('#streamSearch').inputValue(),'ESP32-0000');
    await page.locator('#linkPump').evaluate(form=>form.closest('details').open=true);
    await page.locator('#linkedPump').selectOption({label:'BROWSER-PUMP'});await page.locator('#linkPump button').click();await wait('linked','streamMessage');
    await page.waitForFunction(()=>document.getElementById('streamInsights').textContent.includes('BROWSER-PUMP'));
    console.log('PASS browser: Part 1 registration, retained stream state and newly registered pump link');
    await page.locator('#streamInsights button').first().click();await wait('BROWSER-PUMP','streamTitle');
    await page.locator('#commandOn').click();await wait('ON / OPEN','streamValue');
    await page.locator('#commandOff').click();await wait('OFF / CLOSED','streamValue');
    await page.locator('#undoCommand').click();await wait('ON / OPEN','streamValue');
    await page.locator('#undoCommand').click();await wait('OFF / CLOSED','streamValue');
    page.once('dialog',dialog=>dialog.accept());await page.locator('#shutdownAll').click();await wait('actuators switched OFF');
    await page.locator('#undoCommand').click();await wait('Undid: Shut down all actuators');
    console.log('PASS browser: actual ON/OFF changes, two LIFO undo steps and grouped shutdown restoration');
    await page.locator('#streamSearch').fill('ESP32-0000');await page.locator('#streamFilter button').click();await wait('ESP32-0000','streamTitle');
    await page.locator('#toggleSimulation').evaluate(button=>button.closest('details').open=true);
    await page.locator('#toggleSimulation').click();await wait('Stop','toggleSimulation');
    await page.waitForFunction(async()=>{const q=await(await fetch('/api/stream/queue')).json();return q.admitted>=29;});
    await page.locator('#toggleSimulation').click();await wait('Start','toggleSimulation');
    console.log('PASS browser: HTTP simulator starts, posts telemetry and stops');
    if(process.env.SMARTX_SCREENSHOT_DIRECTORY){fs.mkdirSync(process.env.SMARTX_SCREENSHOT_DIRECTORY,{recursive:true});await page.screenshot({path:path.join(process.env.SMARTX_SCREENSHOT_DIRECTORY,'desktop.png'),fullPage:true});}
    await page.setViewportSize({width:390,height:844});await delay(200);
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1),'mobile page should not overflow horizontally');
    if(process.env.SMARTX_SCREENSHOT_DIRECTORY)await page.screenshot({path:path.join(process.env.SMARTX_SCREENSHOT_DIRECTORY,'mobile.png'),fullPage:true});
    assert.deepEqual(errors,[]);console.log('PASS browser: mobile layout and no uncaught JavaScript errors');
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();processApi.kill('SIGTERM');await new Promise(resolve=>processApi.once('exit',resolve));fs.rmSync(directory,{recursive:true,force:true});});
