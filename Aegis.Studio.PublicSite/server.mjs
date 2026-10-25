import { createIntakeApi } from './intake-api.mjs';
import http from 'node:http';
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
      if(req.method==='GET'&&url.pathname==='/api/session'){
        json(res,200,{demo:false,user:null,authenticationAvailable:false});return;
      }
      if(req.method==='GET'&&url.pathname==='/api/client-galleries'){
        json(res,401,{message:'Verified client sign-in is required.'});return;
      }
      if(req.method==='POST'&&url.pathname==='/api/v1/client-link-proofs/redeem'){
        json(res,401,{message:'Verified client sign-in is required before linking an account.'});return;
      }
      if (await intake.handle(req,res,url)) return;
      if (['POST','PUT','DELETE'].includes(req.method)) requireSameOrigin(req, url);
      if(req.method==='POST'&&url.pathname==='/api/logout'){
        json(res,200,{ok:true},{'Set-Cookie':'studio_public_session=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0'});return;
      }
      const galleryPage = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})$/i);
      const galleryItem = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/items\/([0-9a-f-]{36})\/(preview|thumbnail|download)$/i);
      const selection = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/([0-9a-f-]{36})$/i);
      const submit = url.pathname.match(/^\/api\/galleries\/([0-9a-f-]{36})\/selections\/submit$/i);
      if(galleryPage||galleryItem||selection||submit){
        // Stage/publish APIs remain available to Studio, but private client
        // media cannot be read with an unverified or obsolete mock cookie.
        json(res,401,{message:'Verified client sign-in is required.'});return;
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


