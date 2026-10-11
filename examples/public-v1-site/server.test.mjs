import {test} from 'node:test';import assert from 'node:assert/strict';import {createHmac} from 'node:crypto';import {allowedPath,sign,verifyWebhook} from './server.mjs';
test('reference site refuses private routes and signs exact V1 body',()=>{assert.equal(allowedPath('/api/jobs'),false);assert.equal(allowedPath('/api/public/v1/bookings'),true);assert.notEqual(sign('secret','POST','/api/public/v1/bookings','123','nonce',Buffer.from('{}'),'key'),sign('secret','POST','/api/public/v1/bookings','123','nonce',Buffer.from('{"clientName":"changed"}'),'key'));});
test('webhook receipt requires signed body and bounded timestamp',()=>{const now=Date.now(),timestamp=String(Math.floor(now/1000)),id='11111111-1111-1111-1111-111111111111',body='{"id":"'+id+'"}',signature=createHmac('sha256','secret').update(timestamp+'\n'+id+'\n'+body).digest('hex');assert.equal(verifyWebhook('secret',timestamp,id,body,now)(signature),true);assert.equal(verifyWebhook('secret',timestamp,id,body+' ',now)(signature),false);assert.equal(verifyWebhook('secret',timestamp,id,body,now+301000),false);});
test('reference proxy keeps credentials server-side and acknowledges webhook replays once',async t=>{
 const {createServer}=await import('./server.mjs');const {mkdtemp,readFile,rm}=await import('node:fs/promises');const {tmpdir}=await import('node:os');const {join}=await import('node:path');
 const dir=await mkdtemp(join(tmpdir(),'studio-v1-test-')),log=join(dir,'events.jsonl');const originalFetch=globalThis.fetch;
 const server=createServer({STUDIO_BASE_URL:'https://stub.test',STUDIO_CLIENT:'website',STUDIO_SECRET:'server-only-secret',STUDIO_WEBHOOK_SECRET:'hook-secret',STUDIO_WEBHOOK_LOG:log});
 t.after(async()=>{globalThis.fetch=originalFetch;await new Promise(resolve=>server.close(resolve));await rm(dir,{recursive:true,force:true});});
 await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
 const origin='http://127.0.0.1:'+server.address().port;let forwarded=0;
 globalThis.fetch=async(url,options)=>{if(new URL(url).host!=='stub.test')return originalFetch(url,options);forwarded++;assert.equal(options.headers['X-Studio-Signature'],sign('server-only-secret',options.method,new URL(url).pathname+new URL(url).search,options.headers['X-Studio-Timestamp'],options.headers['X-Studio-Nonce'],options.body||Buffer.alloc(0),options.headers['Idempotency-Key']||''));return new Response('{"reference":"test"}',{status:201,headers:{'Content-Type':'application/json'}});};
 let response=await originalFetch(origin);assert.equal(response.status,200);assert.doesNotMatch(await response.text(),/server-only-secret/);
 response=await originalFetch(origin+'/api/public/v1/bookings',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json','Idempotency-Key':'request-1'},body:'{}'});assert.equal(response.status,201);assert.equal(forwarded,1);
 response=await originalFetch(origin+'/api/jobs');assert.equal(response.status,404);assert.equal(forwarded,1);
 const id='11111111-1111-1111-1111-111111111111',payload=JSON.stringify({schemaVersion:1,id,type:'booking.received',data:{reference:id}}),timestamp=String(Math.floor(Date.now()/1000)),signature=createHmac('sha256','hook-secret').update(timestamp+'\n'+id+'\n'+payload).digest('hex');
 const options={method:'POST',headers:{'Content-Type':'application/json','X-Studio-Event-Id':id,'X-Studio-Timestamp':timestamp,'X-Studio-Signature':signature},body:payload};
 assert.equal((await originalFetch(origin+'/webhooks/studio',options)).status,204);assert.equal((await originalFetch(origin+'/webhooks/studio',options)).status,204);
 assert.equal((await readFile(log,'utf8')).trim().split('\n').length,1);
});
