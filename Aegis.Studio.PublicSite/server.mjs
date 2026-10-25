import { createIntakeApi } from './intake-api.mjs';
import { ClientAuthentication } from './client-auth.mjs';
import http from 'node:http';
import { pipeline } from 'node:stream/promises';
import { createHash, createHmac, randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const ROOT = new URL('./public/', import.meta.url);
export function sign(secret, method, path, timestamp, nonce, body, key = '') {
  const digest = createHash('sha256').update(body).digest('hex');
  return createHmac('sha256', secret).update([method.toUpperCase(), path, timestamp, nonce, digest, key].join('\n')).digest('hex');
}
function json(res, status, value, headers = {}) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...headers });
  res.end(JSON.stringify(value));
}
async function readBody(req, limit = 65536) {
  const chunks = []; let size = 0;
  for await (const chunk of req) { size += chunk.length; if (size > limit) throw Object.assign(new Error('Request is too large.'), { status: 413 }); chunks.push(chunk); }
  return Buffer.concat(chunks);
}
function requireSameOrigin(req, url) {
  const origin = req.headers.origin;
  if (!origin || new URL(origin).host !== req.headers.host) throw Object.assign(new Error('Request origin was rejected.'), { status: 403 });
}
export function createServer(env = process.env) {
  const secret = env.SESSION_SECRET;
  if (!secret || secret.length < 32) throw new Error('Set SESSION_SECRET to at least 32 characters.');
  // The public site accepts contact-based requests, but will not authenticate
  // gallery viewers until a real, validated identity provider is configured.
  if(env.PUBLIC_DEMO_MODE==='true'||env.PUBLIC_REFERENCE_DEMO_MODE==='true')
    throw new Error('Mock gallery sign-in is removed.');
  const webHostname=String(env.PUBLIC_WEB_HOST||'web.bryanstudiosltd.com').toLowerCase();
  const apiHostname=String(env.PUBLIC_API_HOST||'api.bryanstudiosltd.com').toLowerCase();
  // Trust the validated Host header only. Never use X-Forwarded-Host to
  // construct OAuth redirects or to choose an authorization policy.
  const hostAllowlist=new Set((env.PUBLIC_ALLOWED_HOSTS||'').split(',').map(x=>x.trim().toLowerCase()).filter(Boolean));
  if(env.NODE_ENV==='production'&&!hostAllowlist.has(webHostname)||env.NODE_ENV==='production'&&!hostAllowlist.has(apiHostname))
    throw new Error('PUBLIC_ALLOWED_HOSTS must include both the web and API hostnames in production.');
  function hostname(req) {
    const raw=String(req.headers.host||'').toLowerCase();
    if(!raw||raw.includes('/')||raw.includes('\\')||raw.includes('@')||raw.includes(','))return null;
    try{
      const u=new URL('http://'+raw),host=u.hostname.toLowerCase();
      if(u.username||u.password||!host)return null;
      if(hostAllowlist.size)return hostAllowlist.has(host)?host:null;
      return env.NODE_ENV!=='production'&&['localhost','127.0.0.1','[::1]'].includes(host)?host:null;
    }catch{return null;}
  }
  const intake=createIntakeApi(env,readBody,json);
  const clientAuth=new ClientAuthentication(env,intake.store);
  // Never enable authenticated client viewing in production without protected previews.
  if(env.NODE_ENV==='production'&&clientAuth.providers().length>0&&
     env.PUBLIC_WATERMARK_PROCESSING_ENABLED!=='true')
    throw new Error('Watermarked previews must be enabled before production client authentication.');
  if(env.NODE_ENV==='production'&&clientAuth.providers().length>0&&env.GALLERY_STORAGE_MODE!=='s3')
    throw new Error('Private S3 media storage must be configured for production client galleries.');

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Content-Security-Policy', "default-src 'self'; img-src 'self' blob: data: https://images-pw.pixieset.com; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
    try {
      const host=hostname(req);
      if(!host){json(res,421,{message:'Unrecognized website hostname.'});return;}
      if (req.method === 'GET' && url.pathname === '/health') { res.writeHead(200, { 'Cache-Control': 'no-store' }).end('ok'); return; }
      if (req.method === 'GET' && url.pathname === '/' && host===apiHostname) {
        json(res,200,{service:'Bryan\'s Studio API',status:'available'});return;
      }
      if (req.method === 'GET' && url.pathname === '/') { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' }); res.end(await readFile(new URL('index.html', ROOT))); return; }
      if (req.method === 'GET' && ['/styles.css', '/app.js', '/roster-editor.js', '/workday-web.js'].includes(url.pathname)) {
        const type = url.pathname.endsWith('.css') ? 'text/css; charset=utf-8' : 'text/javascript; charset=utf-8';
        res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'public, max-age=300' }); res.end(await readFile(new URL(url.pathname.slice(1), ROOT))); return;
      }
      if(await clientAuth.handle(req,res,url,host,json))return;
      const user=clientAuth.current(req);
      if(req.method==='GET'&&url.pathname==='/api/session'){
        json(res,200,{demo:false,user:user?{name:user.name,email:user.email,provider:user.provider,linked:Boolean(user.clientId)}:null,
          authenticationAvailable:clientAuth.providers().length>0});return;
      }
      if(req.method==='GET'&&url.pathname==='/api/client-galleries'){
        if(!user||!user.clientId){json(res,401,{message:'Verified client identity linking is required.'});return;}
        const rows=intake.store.db.prepare('SELECT id,payload FROM galleries WHERE client_id=?').all(user.clientId);
        json(res,200,{items:rows.map(x=>JSON.parse(x.payload)).filter(x=>x.published).map(x=>({reference:x.id,title:x.title}))});return;
      }
      if(req.method==='POST'&&url.pathname==='/api/v1/client-link-proofs/redeem'){
        requireSameOrigin(req,url);
        if(!user){json(res,401,{message:'Sign in before linking a Studio Client ID.'});return;}
        const payload=JSON.parse((await readBody(req,4096)).toString());
        const linked=intake.store.redeemClientLinkChallenge(payload.code,user.provider,user.subject);
        json(res,200,{status:'Linked',verifiedAt:linked.verifiedAt});return;
      }
      if (await intake.handle(req,res,url)) return;
      if (['POST','PUT','DELETE'].includes(req.method)) requireSameOrigin(req, url);
      if(req.method==='POST'&&url.pathname==='/api/logout'){
        clientAuth.logout(req,res);return;
      }
      const galleryPage = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})$/i);
      const galleryItem = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/items\/([0-9a-f-]{36})\/(preview|thumbnail|download)$/i);
      const selection = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/([0-9a-f-]{36})$/i);
      const submit = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/submit$/i);
      if(galleryPage||galleryItem||selection||submit){
        const galleryId=(galleryPage||galleryItem||selection||submit)[1];
        if(!user?.clientId){json(res,401,{message:'Verified client linkage required.'});return;}
        const row=intake.store.db.prepare('SELECT client_id,payload FROM galleries WHERE id=?').get(galleryId);
        if(!row||row.client_id!==user.clientId){json(res,404,{message:'Gallery not found.'});return;}
        const gallery=JSON.parse(row.payload);
        if(!gallery.published){json(res,404,{message:'Gallery is not published.'});return;}
        if(galleryPage&&req.method==='GET'){
          const selected=new Set(intake.store.db.prepare('SELECT id FROM selections WHERE gallery_id=? AND selected=1').all(galleryId).map(x=>x.id));
          json(res,200,{reference:galleryId,publicationId:galleryId,title:gallery.title,finalDeliveryAvailable:false,
            items:gallery.items.map(x=>({...x,selected:selected.has(x.versionId)})),nextCursor:null});return;
        }
        if(galleryItem&&req.method==='GET'){
          // Original media must never be available using a client session.
          if(galleryItem[3]==='download'){json(res,403,{message:'Original files require privileged integration authorization.'});return;}
          if(!gallery.items.some(x=>x.versionId===galleryItem[2])){json(res,404,{message:'Photograph not found.'});return;}
          const legacy=intake.store.db.prepare('SELECT content,type FROM gallery_previews WHERE gallery_id=? AND id=?').get(galleryId,galleryItem[2]);
          const preview=await intake.mediaStorage.get(galleryId,galleryItem[2],'preview')||legacy;
          if(!preview||(!preview.stream&&!preview.content?.length)){json(res,503,{message:'Protected preview is not ready.'});return;}
          res.writeHead(200,{'Content-Type':preview.type,'Cache-Control':'private, no-store','X-Content-Type-Options':'nosniff'});
          if(preview.stream)await pipeline(preview.stream,res);else res.end(preview.content);return;
        }
        if(selection&&req.method==='PUT'){
          const payload=JSON.parse((await readBody(req,4096)).toString());
          if(typeof payload.selected!=='boolean'||!gallery.items.some(x=>x.versionId===selection[2])){
            json(res,400,{message:'Invalid photograph selection.'});return;
          }
          intake.store.db.prepare('INSERT INTO selections VALUES(?,?,?) ON CONFLICT(gallery_id,id) DO UPDATE SET selected=excluded.selected')
            .run(galleryId,selection[2],payload.selected?1:0);
          json(res,200,{versionId:selection[2],selected:payload.selected});return;
        }
        if(submit&&req.method==='POST'){
          const result=intake.store.submitGallerySelections(user.provider+':'+user.subject,user.clientId,galleryId,
            String(req.headers['idempotency-key']||''));
          json(res,200,result);return;
        }
        json(res,405,{message:'Method not allowed.'});return;
      }
      json(res, 404, { message: 'Not found.' });
    } catch (error) {
      if (!res.headersSent) json(res, error.status || 503, { code:error.code || 'service_unavailable', message: error.status ? error.message : 'Public service unavailable. Retry using the same request key.',fieldErrors:error.fields || {},correlationId:randomUUID() },error.status===429?{'Retry-After':'60'}:{});
      else res.end();
    }
  });
  server.on('close',()=>{intake.workdayStore.close();intake.store.close();});
  return server;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const server = createServer();
  const host=process.env.HOST||'127.0.0.1',port=Number(process.env.PORT||8080);
  server.listen(port,host,()=>console.log(`Aegis Studio Public Site listening on ${host}:${port}`));
}


