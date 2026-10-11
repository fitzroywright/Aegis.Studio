import http from 'node:http';
import {createHash,createHmac,randomUUID,timingSafeEqual} from 'node:crypto';
import {readFile,open} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';

export function sign(secret,method,path,timestamp,nonce,body,key=''){
 const digest=createHash('sha256').update(body).digest('hex');
 return createHmac('sha256',secret).update([method.toUpperCase(),path,timestamp,nonce,digest,key].join('\n')).digest('hex');
}
export function verifyWebhook(secret,timestamp,id,body,now=Date.now()){
 if(!/^\d+$/.test(timestamp)||Math.abs(now/1000-Number(timestamp))>300||!/^[0-9a-f-]{36}$/.test(id))return false;
 const expected=createHmac('sha256',secret).update(timestamp+'\n'+id+'\n'+body).digest('hex');
 return supplied=>{if(!/^[0-9a-f]{64}$/.test(supplied))return false;return timingSafeEqual(Buffer.from(expected,'hex'),Buffer.from(supplied,'hex'));};
}
export function allowedPath(path){return /^\/api\/public\/v1\/(bookings(?:\/[0-9a-f-]{36})?|galleries\/[0-9a-f-]{36}(?:\/items\/[0-9a-f-]{36}\/(?:preview|thumbnail|download)|\/selections\/(?:[0-9a-f-]{36}|submit))?)$/.test(path);}
export function createServer(env=process.env){
 const studio=new URL(env.STUDIO_BASE_URL||'https://studio.example');
 if(studio.protocol!=='https:')throw Error('STUDIO_BASE_URL must use HTTPS.');
 const client=env.STUDIO_CLIENT,secret=env.STUDIO_SECRET;
 if(!client||!secret)throw Error('Set STUDIO_CLIENT and STUDIO_SECRET on the server.');
 const log=env.STUDIO_WEBHOOK_LOG||'studio-webhooks.jsonl';
 const seen=readFile(log,'utf8').then(text=>new Map(text.split('\n').filter(Boolean).map(line=>{const event=JSON.parse(line);return [event.id,JSON.stringify(event)];}))).catch(error=>{if(error.code==='ENOENT')return new Map();throw error;});
 let writes=Promise.resolve();
 return http.createServer(async(req,res)=>{
  try{
   const url=new URL(req.url,'http://reference.local');
   res.setHeader('Cache-Control','no-store');
   res.setHeader('X-Content-Type-Options','nosniff');
   if(req.method==='GET'&&url.pathname==='/'){
    res.setHeader('Content-Type','text/html; charset=utf-8');res.end(await readFile(new URL('./index.html',import.meta.url)));return;
   }
   const chunks=[];let length=0;for await(const chunk of req){length+=chunk.length;if(length>65536){res.writeHead(413).end();return;}chunks.push(chunk);}const body=Buffer.concat(chunks);
   if(url.pathname==='/webhooks/studio'&&req.method==='POST'){
    const validate=verifyWebhook(env.STUDIO_WEBHOOK_SECRET||'',String(req.headers['x-studio-timestamp']||''),String(req.headers['x-studio-event-id']||''),body.toString('utf8'));
    if(!env.STUDIO_WEBHOOK_SECRET||!validate||!validate(String(req.headers['x-studio-signature']||''))){res.writeHead(401).end();return;}
    const event=JSON.parse(body);if(event.id!==req.headers['x-studio-event-id']||event.schemaVersion!==1){res.writeHead(400).end();return;}
    // Serialize durable deduplication; acknowledge only after the event is saved.
    const normalized=JSON.stringify(event);
    const write=writes.catch(()=>{}).then(async()=>{const known=await seen;if(known.has(event.id)){if(known.get(event.id)!==normalized)throw Error('Event ID reused for different facts');return;}const file=await open(log,'a');try{await file.writeFile(normalized+'\n');await file.sync();}finally{await file.close();}known.set(event.id,normalized);});
    writes=write;await write;res.writeHead(204).end();return;
   }
   if(!allowedPath(url.pathname)||!['GET','POST','PUT'].includes(req.method)){res.writeHead(404).end();return;}
   if(req.method!=='GET'){
    const origin=req.headers.origin;
    if(!origin||new URL(origin).host!==req.headers.host){res.writeHead(403).end();return;}
   }
   const timestamp=String(Math.floor(Date.now()/1000)),nonce=randomUUID(),key=String(req.headers['idempotency-key']||''),path=url.pathname+url.search;
   const headers={'X-Studio-Client':client,'X-Studio-Timestamp':timestamp,'X-Studio-Nonce':nonce,'X-Studio-Signature':sign(secret,req.method,path,timestamp,nonce,body,key)};
   if(req.headers.authorization)headers.Authorization=req.headers.authorization;
   if(key)headers['Idempotency-Key']=key;
   if(body.length)headers['Content-Type']='application/json';
   const upstream=await fetch(new URL(path,studio),{method:req.method,headers,body:body.length?body:undefined,redirect:'error',signal:AbortSignal.timeout(30000)});
   res.writeHead(upstream.status,{'Content-Type':upstream.headers.get('content-type')||'application/json'});
   res.end(Buffer.from(await upstream.arrayBuffer()));
  }catch{if(!res.headersSent)res.writeHead(503,{'Content-Type':'application/json'});res.end(JSON.stringify({code:'reference_unavailable',message:'Reference service unavailable. Your request key is preserved.'}));}
 });
}
if(process.argv[1]&&fileURLToPath(import.meta.url)===process.argv[1])createServer().listen(Number(process.env.PORT||8080),'127.0.0.1',()=>console.log('Reference website: http://127.0.0.1:'+(process.env.PORT||8080)));
