// Local real-browser acceptance. Set PLAYWRIGHT_MODULE to a locally installed Playwright module.
// Never connects to production. Requires npm ci in Aegis.Studio.PublicSite.
import {pathToFileURL} from 'node:url';
import {mkdtempSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';import {join} from 'node:path';import assert from 'node:assert/strict';
import {createServer} from '../Aegis.Studio.PublicSite/server.mjs';
import {WorkDayExchangeStore} from '../Aegis.Studio.PublicSite/workday-exchange-store.mjs';
const {chromium}=await import(process.env.PLAYWRIGHT_MODULE?pathToFileURL(process.env.PLAYWRIGHT_MODULE).href:'playwright');
const {default:sharp}=await import('../Aegis.Studio.PublicSite/node_modules/sharp/dist/index.mjs');
const dir=mkdtempSync(join(tmpdir(),'aegis386-browser-')),db=join(dir,'site.sqlite');
const assignment='44444444-4444-4444-8444-444444444444',username='local.photographer',password='synthetic-local-only-passphrase';
const store=new WorkDayExchangeStore(db);const account=store.provisionAccount({userName:username,displayName:'Local photographer'});
store.enroll(account.enrollmentToken,password);store.publishSnapshot(username,1,{today:[{id:assignment,title:'Local RAW acceptance'}],upcoming:[]});store.close();
const raw=Buffer.alloc(9*1024*1024);raw.write('II');raw.writeUInt16LE(42,2);raw.writeUInt32LE(8,4);
const tags=[[256,4,1,1],[257,4,1,1],[262,3,1,32803],[273,4,1,200],[279,4,1,1],[50706,1,4,0x401]];
raw.writeUInt16LE(tags.length,8);tags.forEach(([tag,type,n,value],i)=>{const p=10+12*i;raw.writeUInt16LE(tag,p);raw.writeUInt16LE(type,p+2);raw.writeUInt32LE(n,p+4);raw.writeUInt32LE(value,p+8);});raw[200]=99;
const rawPath=join(dir,'capture.dng'),secondPath=join(dir,'second.jpg');writeFileSync(rawPath,raw);
writeFileSync(secondPath,await sharp({create:{width:20,height:20,channels:3,background:'#123456'}}).jpeg().toBuffer());
const server=createServer({SESSION_SECRET:'synthetic-browser-session-secret-32-characters',INTAKE_DB_PATH:db,WORKDAY_UPLOAD_ROOT:join(dir,'private')});
await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
let browser;
try{
 browser=await chromium.launch({headless:true,executablePath:process.env.CHROME_PATH||'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'});
 const page=await browser.newPage(),errors=[],confirmed=[];page.on('pageerror',e=>errors.push(e.message));
 page.on('response',r=>{if(r.url().includes('/chunks/')&&r.status()===200)confirmed.push(Number(r.url().split('/').at(-1)));});
 await page.goto(origin+'/#workday');await page.locator('#workday-signin input[name=userName]').fill(username);await page.locator('#workday-signin input[name=password]').fill(password);await page.locator('#workday-signin button[type=submit]').click();
 await page.locator('#workday-panel').waitFor({state:'visible'});
 const choose=async()=>{await page.locator('#workday-assignment').selectOption(assignment);await page.waitForFunction(()=>document.querySelector('#workday-gallery-message').textContent==='');};
 await choose();
 // A real XHR interruption after chunk 0; keep the retained partial upload through reload.
 let interrupt=true;await page.route('**/chunks/1',async route=>{if(interrupt){interrupt=false;await route.abort('internetdisconnected');}else await route.continue();});
 await page.locator('#workday-photos').setInputFiles(rawPath);await page.locator('#workday-upload').click();
 await page.waitForFunction(()=>document.querySelector('#workday-status').textContent.includes('could not be confirmed'));
 assert.deepEqual(confirmed,[0]);assert.equal(await page.locator('button[aria-label^="Retry "]').count(),1);
 await page.reload();await page.locator('#workday-panel').waitFor({state:'visible'});await choose();
 await page.locator('#workday-photos').setInputFiles([rawPath,secondPath]);
 await page.evaluate(()=>{window.__rawProgress=[];new MutationObserver(()=>{for(const p of document.querySelectorAll('#workday-photo-grid progress'))window.__rawProgress.push(p.value);}).observe(document.querySelector('#workday-photo-grid'),{subtree:true,childList:true,attributes:true});});
 await page.locator('#workday-upload').click();await page.waitForFunction(()=>document.querySelector('#workday-status').textContent==='2 photographs queued for Studio.');
 assert.deepEqual(confirmed,[0,1,2]);assert.ok((await page.evaluate(()=>window.__rawProgress)).some(x=>x>0));
 await page.reload();await page.locator('#workday-panel').waitFor({state:'visible'});await choose();
 await page.waitForFunction(()=>document.querySelectorAll('#workday-photo-grid figure').length===2);
 assert.equal(await page.locator('#workday-photo-grid figure').count(),2);
 // Explicit removal after a network interruption cancels the server's incomplete session.
 const cancelled=join(dir,'cancel.dng');writeFileSync(cancelled,raw);interrupt=true;
 await page.locator('#workday-photos').setInputFiles(cancelled);await page.locator('#workday-upload').click();
 await page.waitForFunction(()=>document.querySelector('#workday-status').textContent.includes('could not be confirmed'));
 const cancelledId=await page.locator('#workday-photo-grid figure').filter({hasText:'cancel.dng'}).getAttribute('data-upload-id');
 await page.getByRole('button',{name:'Remove cancel.dng from selection',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('#workday-photo-grid figure').length===2);
 const cancellation=await page.request.get(origin+'/api/v1/workday/assignments/'+assignment+'/upload-sessions/'+cancelledId);
 assert.equal((await cancellation.json()).state,'Cancelled');
 // Browser pause during hashing must settle the batch and preserve the selected original.
 const paused=join(dir,'pause.dng');writeFileSync(paused,raw);await page.locator('#workday-photos').setInputFiles(paused);
 await page.evaluate(()=>{document.querySelector('#workday-upload').click();document.querySelector('#workday-pause').click();});
 await page.waitForFunction(()=>!document.querySelector('#workday-clear').disabled);
 assert.equal(await page.locator('button[aria-label^="Retry "]').count(),1);
 await page.locator('#workday-clear').click();assert.equal(await page.locator('#workday-photo-grid figure').count(),2);
 const signingOut=join(dir,'signout.dng');writeFileSync(signingOut,raw);await page.locator('#workday-photos').setInputFiles(signingOut);
 await page.evaluate(()=>{document.querySelector('#workday-upload').click();document.querySelector('#workday-signout').click();});await page.locator('#workday-login').waitFor({state:'visible'});assert.equal(await page.locator('#workday-photo-grid figure').count(),0);
 assert.deepEqual(errors,[]);
 console.log(JSON.stringify({browser:'local Chrome',incrementalHashWorker:'passed',interruptionAndReloadResume:'passed',confirmedChunks:confirmed,serverCancellation:'passed',multiFileRawAndJpeg:'passed',progress:'passed',galleryAfterReload:2,pauseAndRemove:'passed',signOutPrivacy:'passed',javascriptErrors:errors},null,2));
}finally{if(browser)await browser.close();server.closeAllConnections();await new Promise(r=>server.close(r));rmSync(dir,{recursive:true,force:true});}
