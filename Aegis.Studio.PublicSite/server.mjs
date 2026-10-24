import { createIntakeApi } from './intake-api.mjs';
import http from 'node:http';
import https from 'node:https';
import { createHash, createHmac, randomUUID, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const ROOT = new URL('./public/', import.meta.url);
const DEMO_USERS = Object.freeze({
  maya: { id: 'demo-google-maya', name: 'Maya Chen', provider: 'Google', email: 'maya.chen@example.test', galleryReference: '10000000-0000-4000-8000-000000000001' },
  jordan: { id: 'demo-facebook-jordan', name: 'Jordan Lee', provider: 'Facebook', email: 'jordan.lee@example.test', galleryReference: '20000000-0000-4000-8000-000000000002' },
});
const DEMO_GALLERIES = Object.freeze({
  [DEMO_USERS.maya.galleryReference]: { title: 'Maya & Daniel · Engagement', client: 'Maya Chen', items: [
    { versionId: '10000000-0000-4000-8000-000000000011', fileName: 'golden-hour-01.jpg', color: '#dbb37f', caption: 'Golden hour' },
    { versionId: '10000000-0000-4000-8000-000000000012', fileName: 'garden-walk-08.jpg', color: '#6b866c', caption: 'Garden walk' },
    { versionId: '10000000-0000-4000-8000-000000000013', fileName: 'portraits-14.jpg', color: '#d0a9a0', caption: 'Together' },
  ] },
  [DEMO_USERS.jordan.galleryReference]: { title: 'Jordan Lee · Graduation', client: 'Jordan Lee', items: [
    { versionId: '20000000-0000-4000-8000-000000000021', fileName: 'campus-portrait-02.jpg', color: '#71839b', caption: 'Campus portrait' },
    { versionId: '20000000-0000-4000-8000-000000000022', fileName: 'family-moment-11.jpg', color: '#c39a79', caption: 'Family moment' },
  ] },
});

export function sign(secret, method, path, timestamp, nonce, body, key = '') {
  const digest = createHash('sha256').update(body).digest('hex');
  return createHmac('sha256', secret).update([method.toUpperCase(), path, timestamp, nonce, digest, key].join('\n')).digest('hex');
}
function json(res, status, value, headers = {}) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...headers });
  res.end(JSON.stringify(value));
}
function cookieFlags(env) { return `Path=/; HttpOnly; SameSite=Lax; Max-Age=28800${env.NODE_ENV === 'production' ? '; Secure' : ''}`; }
function issueSession(user, secret) {
  const payload = Buffer.from(JSON.stringify({ id: user.id, exp: Date.now() + 8 * 60 * 60 * 1000 })).toString('base64url');
  const mac = createHmac('sha256', secret).update(payload).digest('base64url');
  return `${payload}.${mac}`;
}
function readSession(req, secret) {
  const cookies = (req.headers.cookie || '').split(';').map(x => x.trim());
  const token = cookies.find(x => x.startsWith('studio_public_session='))?.slice('studio_public_session='.length);
  if (!token) return null;
  const [payload, mac, ...extra] = token.split('.');
  if (!payload || !mac || extra.length) return null;
  const expected = createHmac('sha256', secret).update(payload).digest();
  let supplied;
  try { supplied = Buffer.from(mac, 'base64url'); } catch { return null; }
  if (expected.length !== supplied.length || !timingSafeEqual(expected, supplied)) return null;
  try {
    const value = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8'));
    if (value.exp <= Date.now()) return null;
    return Object.values(DEMO_USERS).find(user => user.id === value.id) || null;
  } catch { return null; }
}
async function readBody(req, limit = 65536) {
  const chunks = []; let size = 0;
  for await (const chunk of req) { size += chunk.length; if (size > limit) throw Object.assign(new Error('Request is too large.'), { status: 413 }); chunks.push(chunk); }
  return Buffer.concat(chunks);
}
function safeMobilePath(pathname,prefix){
  if(!pathname.startsWith(prefix+'/'))return false;
  const tail=pathname.slice(prefix.length+1).split('/');
  return tail.length>0&&tail.every(part=>part.length>0&&[...part].every(ch=>/[A-Za-z0-9._~%-]/.test(ch)));
}
function validUuid(value){
  const parts=String(value||'').split('-');
  return parts.length===5&&[8,4,4,4,12].every((size,index)=>parts[index].length===size&&[...parts[index]].every(ch=>/[0-9a-f]/i.test(ch)));
}
function safeMobileSegment(value){return value.length>0&&[...value].every(ch=>/[A-Za-z0-9._~-]/.test(ch));}
export function mapWorkDayStudioPath(pathname,method){
  method=String(method||'').toUpperCase();
  const parts=pathname.split('/');
  if(parts[1]!=='api'||parts[2]!=='v1')return null;
  let allowed=false;
  if(parts[3]==='auth'&&parts.length===5){
    allowed=(parts[4]==='login'&&method==='POST')||(parts[4]==='logout'&&method==='POST')||(parts[4]==='me'&&method==='GET');
  }else if(parts[3]==='workday'){
    const tail=parts.slice(4);
    allowed=(tail.length===1&&tail[0]==='bootstrap'&&method==='GET')
      ||(tail.length===1&&tail[0]==='device-session'&&method==='POST')
      ||(tail.length===2&&tail[0]==='device-session'&&tail[1]==='revoke'&&method==='POST')
      ||(tail.length===1&&tail[0]==='client-observation'&&method==='POST')
      ||(tail.length===1&&tail[0]==='operations'&&method==='POST')
      ||(tail.length===1&&tail[0]==='availability'&&['GET','POST'].includes(method))
      ||(tail.length===1&&tail[0]==='time-summary'&&method==='GET')
      ||(tail.length===3&&tail[0]==='assignments'&&validUuid(tail[1])&&tail[2]==='uploads'&&method==='POST')
      ||(tail.length===3&&tail[0]==='assignments'&&validUuid(tail[1])&&tail[2]==='gallery-status'&&method==='GET');
  }else if(parts[3]==='messages'&&parts[4]==='threads'){
    allowed=(parts.length===5&&method==='GET')
      ||(parts.length===6&&validUuid(parts[5])&&['GET','POST'].includes(method))
      ||(parts.length===7&&safeMobileSegment(parts[5])&&validUuid(parts[6])&&method==='POST');
  }else if(parts[3]==='notifications'&&parts.length===4&&method==='GET')allowed=true;
  else if(parts[3]==='conversations'&&parts.length===6&&validUuid(parts[4]))
    allowed=(parts[5]==='sending-identities'&&method==='GET')||(parts[5]==='messages'&&method==='POST');
  return allowed?pathname.replace('/api/v1','/api'):null;
}
function proxyWorkDay(req,res,url,env){
  const upstreamText=String(env.WORKDAY_STUDIO_ORIGIN||'');
  let origin;
  try{origin=new URL(upstreamText);if(origin.protocol!=='https:'||origin.pathname!=='/'||origin.search||origin.hash||origin.username||origin.password)throw Error();}
  catch{json(res,503,{code:'workday_upstream_unavailable',message:'WorkDay is not connected to Studio.'});return;}
  const studioPath=mapWorkDayStudioPath(url.pathname,req.method);
  if(!studioPath){json(res,404,{code:'not_found',message:'WorkDay operation not found.'});return;}
  const forwarded={host:origin.host,'x-forwarded-host':req.headers.host||'','x-forwarded-proto':'https','x-forwarded-for':req.socket.remoteAddress||''};
  for(const name of ['accept','accept-encoding','content-type','content-length','transfer-encoding','cookie','authorization','idempotency-key','user-agent'])if(req.headers[name])forwarded[name]=req.headers[name];
  if(url.pathname.startsWith('/api/v1/workday/')&&req.headers['x-aegis-workday-token'])forwarded['x-aegis-workday-token']=req.headers['x-aegis-workday-token'];
  const connectAddress=String(env.WORKDAY_STUDIO_CONNECT_ADDRESS||'').trim();
  let timedOut=false;
  const upstream=https.request({hostname:origin.hostname,port:origin.port||443,servername:origin.hostname,method:req.method,path:studioPath+url.search,headers:forwarded,
    ...(connectAddress?{lookup:(_host,_options,callback)=>callback(null,connectAddress,connectAddress.includes(':')?6:4)}:{})},response=>{
      const headers={};
      for(const name of ['content-type','content-length','content-encoding','content-disposition','cache-control','etag','last-modified','retry-after','set-cookie','location','vary'])if(response.headers[name]!==undefined)headers[name]=response.headers[name];
      headers['Cache-Control']=headers['cache-control']||'no-store';headers['X-Content-Type-Options']='nosniff';
      res.writeHead(response.statusCode||502,headers);response.pipe(res);
    });
  upstream.setTimeout(Number(env.WORKDAY_STUDIO_TIMEOUT_MS||120000),()=>{timedOut=true;upstream.destroy(new Error('WorkDay upstream timeout'));});
  upstream.on('error',error=>{if(!res.headersSent)json(res,timedOut?504:503,{code:timedOut?'workday_upstream_timeout':'workday_upstream_unavailable',message:timedOut?'Studio did not answer the WorkDay request in time.':'Studio WorkDay service is temporarily unavailable.'});else res.destroy(error);});
  req.on('aborted',()=>upstream.destroy());
  req.pipe(upstream);
}

function requireSameOrigin(req, url) {
  const origin = req.headers.origin;
  if (!origin || new URL(origin).host !== req.headers.host) throw Object.assign(new Error('Request origin was rejected.'), { status: 403 });
}
function requireGallery(user, reference) {
  if (!user || user.galleryReference !== reference) throw Object.assign(new Error('Sign in to the client account that owns this gallery.'), { status: 404 });
}
function demoPage(reference, selected) {
  const gallery = DEMO_GALLERIES[reference];
  return { reference, publicationId: reference, title: gallery.title, finalDeliveryAvailable: false,
    items: gallery.items.map(item => ({ versionId: item.versionId, fileName: item.fileName, selected: selected.has(item.versionId) })), nextCursor: null };
}
function svgProof(reference, version) {
  const item = DEMO_GALLERIES[reference]?.items.find(x => x.versionId === version);
  if (!item) return null;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 760"><defs><linearGradient id="g" x2="1" y2="1"><stop stop-color="${item.color}"/><stop offset="1" stop-color="#253b49"/></linearGradient></defs><rect width="1200" height="760" fill="url(#g)"/><circle cx="870" cy="260" r="180" fill="#fff" opacity=".13"/><path d="M0 600 Q280 430 570 610 T1200 540 V760 H0" fill="#182b37" opacity=".5"/><text x="72" y="650" fill="#fff" font-family="serif" font-size="46">${item.caption} · client proof</text><text x="600" y="400" text-anchor="middle" fill="#fff" opacity=".38" font-family="sans-serif" font-size="64" letter-spacing="18">DEMO PROOF</text></svg>`;
}

export function createServer(env = process.env) {
  const secret = env.SESSION_SECRET;
  if (!secret || secret.length < 32) throw new Error('Set SESSION_SECRET to at least 32 characters.');
  const demo = env.PUBLIC_DEMO_MODE === 'true';
  const demoSelections = new Map();
  const intake=createIntakeApi(env,readBody,json);
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
    res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Content-Security-Policy', "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
    try {
      if (req.method === 'GET' && url.pathname === '/health') { res.writeHead(200, { 'Cache-Control': 'no-store' }).end('ok'); return; }
      if (req.method === 'GET' && url.pathname === '/') { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' }); res.end(await readFile(new URL('index.html', ROOT))); return; }
      if (req.method === 'GET' && ['/styles.css', '/app.js', '/roster-editor.js'].includes(url.pathname)) {
        const type = url.pathname.endsWith('.css') ? 'text/css; charset=utf-8' : 'text/javascript; charset=utf-8';
        res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'public, max-age=300' }); res.end(await readFile(new URL(url.pathname.slice(1), ROOT))); return;
      }
      let user = readSession(req, secret);
      if(user){const binding=intake.store.clientForIdentity(user.provider,user.id)||JSON.parse(env.CLIENT_BINDINGS_JSON||'{}')[user.id];if(binding){const candidates=intake.store.db.prepare('SELECT id,payload FROM galleries WHERE client_id=?').all(binding).filter(r=>JSON.parse(r.payload).published);if(candidates.length)user={...user,galleryReference:candidates.at(-1).id};}}
      if (req.method === 'GET' && url.pathname === '/api/session') { json(res, 200, { demo, user: user ? { name: user.name, provider: user.provider, email: user.email, galleryReference: user.galleryReference } : null }); return; }
      if(req.method==='GET'&&url.pathname==='/api/client-galleries'){
        if(!user){json(res,401,{message:'Sign in to view client galleries.'});return;}
        const binding=intake.store.clientForIdentity(user.provider,user.id)||JSON.parse(env.CLIENT_BINDINGS_JSON||'{}')[user.id];
        if(binding){const items=intake.store.db.prepare('SELECT id,payload FROM galleries WHERE client_id=?').all(binding).map(r=>JSON.parse(r.payload)).filter(g=>g.published).map(g=>({reference:g.id,title:g.title}));json(res,200,{items});return;}
        const gallery=demo&&DEMO_GALLERIES[user.galleryReference];json(res,200,{items:gallery?[{reference:user.galleryReference,title:gallery.title}]:[]});return;
      }
      if (req.method==='POST'&&url.pathname==='/api/v1/client-link-proofs/redeem') {
        requireSameOrigin(req,url);
        if(!user){json(res,401,{message:'Sign in with the provider account to redeem this link proof.'});return;}
        const p=JSON.parse((await readBody(req,4096)).toString('utf8'));
        const linked=intake.store.redeemClientLinkChallenge(p.code,user.provider,user.id);
        json(res,200,{status:'Linked',provider:linked.provider,verifiedAt:linked.verifiedAt});return;
      }
      if(mapWorkDayStudioPath(url.pathname,req.method)){proxyWorkDay(req,res,url,env);return;}
      if (await intake.handle(req,res,url)) return;
      if (['POST','PUT','DELETE'].includes(req.method)) requireSameOrigin(req, url);
      if (req.method === 'POST' && url.pathname === '/api/demo-login') {
        if (!demo) { json(res, 404, { message: 'Demo sign-in is disabled.' }); return; }
        const payload = JSON.parse((await readBody(req)).toString('utf8'));
        const target = DEMO_USERS[payload.identity];
        if (!target) { json(res, 400, { message: 'Choose one of the provided demo identities.' }); return; }
        json(res, 200, { user: { name: target.name, provider: target.provider, email: target.email, galleryReference: target.galleryReference } }, { 'Set-Cookie': `studio_public_session=${issueSession(target, secret)}; ${cookieFlags(env)}` }); return;
      }
      if (req.method === 'POST' && url.pathname === '/api/logout') { json(res, 200, { ok: true }, { 'Set-Cookie': `studio_public_session=; ${cookieFlags(env).replace('Max-Age=28800', 'Max-Age=0')}` }); return; }
      const galleryPage = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})$/i);
      const galleryItem = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/items\/([0-9a-f-]{36})\/(preview|thumbnail|download)$/i);
      const selection = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/([0-9a-f-]{36})$/i);
      const submit = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/submit$/i);
      if (galleryPage || galleryItem || selection || submit) {
        const reference = (galleryPage || galleryItem || selection || submit)[1]; const locallyPublished=intake.store.db.prepare('SELECT id FROM galleries WHERE id=?').get(reference);if(demo&&!locallyPublished)requireGallery(user, reference);
        if (demo&&!locallyPublished) {
          if (!DEMO_GALLERIES[reference]) { json(res, 404, { message: 'Gallery not found.' }); return; }
          const selectionKey = `${user.id}:${reference}`;
          if (!demoSelections.has(selectionKey)) demoSelections.set(selectionKey, new Set());
          const selections = demoSelections.get(selectionKey);
          if (galleryPage && req.method === 'GET') { json(res, 200, demoPage(reference, selections)); return; }
          if (galleryItem && req.method === 'GET') {
            const proof = svgProof(reference, galleryItem[2]); if (!proof) { json(res, 404, { message: 'Proof not found.' }); return; }
            res.writeHead(200, { 'Content-Type': 'image/svg+xml; charset=utf-8', 'Cache-Control': 'private, no-store' }); res.end(proof); return;
          }
          if (selection && req.method === 'PUT') {
            const input = JSON.parse((await readBody(req)).toString('utf8'));
            if (typeof input.selected !== 'boolean' || !DEMO_GALLERIES[reference].items.some(item => item.versionId === selection[2])) { json(res, 400, { message: 'Choose a valid proof and selection state.' }); return; }
            if (input.selected) selections.add(selection[2]); else selections.delete(selection[2]);
            json(res, 200, { versionId: selection[2], selected: input.selected, demo: true }); return;
          }
          if (submit && req.method === 'POST') { json(res, 200, { reference: randomUUID(), publicationId: reference, versionIds: [...selections], submittedAt: new Date().toISOString(), demo: true }); return; }
          json(res, 405, { message: 'Method not allowed.' }); return;
        }
        // Serve only locally stored copies. The identity binding is configured server-side,
        // never accepted from an email address or a caller-supplied client ID.
        const binding=intake.store.clientForIdentity(user?.provider,user?.id)||JSON.parse(env.CLIENT_BINDINGS_JSON||'{}')[user?.id];
        const row=intake.store.db.prepare('SELECT client_id,payload FROM galleries WHERE id=?').get(reference);
        if(!user||!binding||!row||row.client_id!==binding){json(res,404,{message:'Gallery not found.'});return;}
        const gallery=JSON.parse(row.payload);if(!gallery.published){json(res,404,{message:'Gallery unavailable.'});return;}
        if(galleryPage&&req.method==='GET'){
          const selected=new Set(intake.store.db.prepare('SELECT id FROM selections WHERE gallery_id=? AND selected=1').all(reference).map(x=>x.id));
          json(res,200,{reference,publicationId:reference,title:gallery.title,finalDeliveryAvailable:gallery.finalDeliveryAvailable,items:gallery.items.map(x=>({...x,selected:selected.has(x.versionId)})),nextCursor:null});return;
        }
        if(galleryItem&&req.method==='GET'){
          if(!gallery.items.some(x=>x.versionId===galleryItem[2])||galleryItem[3]==='download'&&!gallery.finalDeliveryAvailable){json(res,404,{message:'Item unavailable.'});return;}
          const media=intake.store.db.prepare('SELECT type,content FROM media WHERE gallery_id=? AND id=?').get(reference,galleryItem[2]);if(!media){json(res,404,{message:'Item unavailable.'});return;}
          res.writeHead(200,{'Content-Type':media.type,'Cache-Control':'private, no-store','X-Content-Type-Options':'nosniff'});res.end(media.content);return;
        }
        if(selection&&req.method==='PUT'){
          const p=JSON.parse((await readBody(req)).toString());if(typeof p.selected!=='boolean'||!gallery.items.some(x=>x.versionId===selection[2])){json(res,400,{message:'Invalid selection.'});return;}
          intake.store.db.prepare('INSERT INTO selections VALUES(?,?,?) ON CONFLICT(gallery_id,id) DO UPDATE SET selected=excluded.selected').run(reference,selection[2],p.selected?1:0);json(res,200,{versionId:selection[2],selected:p.selected});return;
        }
        if(submit&&req.method==='POST'){
          const ids=intake.store.db.prepare('SELECT id FROM selections WHERE gallery_id=? AND selected=1').all(reference).map(x=>x.id);
          const result=intake.store.replay('gallery:'+user.id,String(req.headers['idempotency-key']||''),{reference,ids},()=>({reference:randomUUID(),publicationId:reference,versionIds:ids,submittedAt:new Date().toISOString()}));json(res,200,result);return;
        }
        json(res,405,{message:'Method not allowed.'});return;
      }
      json(res, 404, { message: 'Not found.' });
    } catch (error) {
      if (!res.headersSent) json(res, error.status || 503, { code:error.code || 'service_unavailable', message: error.status ? error.message : 'Public service unavailable. Retry using the same request key.',fieldErrors:error.fields || {},correlationId:randomUUID() },error.status===429?{'Retry-After':'60'}:{});
      else res.end();
    }
  });
  server.on('close',()=>intake.store.close());
  return server;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const server = createServer();
  const host=process.env.HOST||'127.0.0.1',port=Number(process.env.PORT||8080);
  server.listen(port,host,()=>console.log(`Aegis Studio Public Site listening on ${host}:${port}`));
}


