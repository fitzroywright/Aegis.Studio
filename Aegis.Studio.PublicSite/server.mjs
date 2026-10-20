import { createIntakeApi } from './intake-api.mjs';
import http from 'node:http';
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
function requireSameOrigin(req, url) {
  const origin = req.headers.origin;
  if (!origin || new URL(origin).host !== req.headers.host || new URL(origin).origin !== url.origin) throw Object.assign(new Error('Request origin was rejected.'), { status: 403 });
}
function requireGallery(user, reference) {
  if (!user || user.galleryReference !== reference) throw Object.assign(new Error('Sign in to the client account that owns this gallery.'), { status: 404 });
}
function studioConfig(env) {
  if (!env.STUDIO_BASE_URL || !env.STUDIO_CLIENT || !env.STUDIO_SECRET) throw Object.assign(new Error('Studio API is not configured.'), { status: 503 });
  const base = new URL(env.STUDIO_BASE_URL);
  if (base.protocol !== 'https:') throw new Error('STUDIO_BASE_URL must use HTTPS.');
  return { base, client: env.STUDIO_CLIENT, secret: env.STUDIO_SECRET };
}
async function studioRequest(env, method, path, body = Buffer.alloc(0), key = '', bearer = '') {
  const { base, client, secret } = studioConfig(env);
  const timestamp = String(Math.floor(Date.now() / 1000)), nonce = randomUUID();
  const headers = {
    'X-Studio-Client': client, 'X-Studio-Timestamp': timestamp, 'X-Studio-Nonce': nonce,
    'X-Studio-Signature': sign(secret, method, path, timestamp, nonce, body, key),
  };
  if (key) headers['Idempotency-Key'] = key;
  if (bearer) headers.Authorization = `Bearer ${bearer}`;
  if (body.length) headers['Content-Type'] = 'application/json';
  const response = await fetch(new URL(path, base), { method, headers, body: body.length ? body : undefined, redirect: 'error', signal: AbortSignal.timeout(30000) });
  return response;
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
      if (req.method === 'GET' && ['/styles.css', '/app.js'].includes(url.pathname)) {
        const type = url.pathname.endsWith('.css') ? 'text/css; charset=utf-8' : 'text/javascript; charset=utf-8';
        res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'public, max-age=300' }); res.end(await readFile(new URL(url.pathname.slice(1), ROOT))); return;
      }
      const user = readSession(req, secret);
      if (req.method === 'GET' && url.pathname === '/api/session') { json(res, 200, { demo, user: user ? { name: user.name, provider: user.provider, email: user.email, galleryReference: user.galleryReference } : null }); return; }
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
        const reference = (galleryPage || galleryItem || selection || submit)[1]; requireGallery(user, reference);
        if (demo) {
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
        const grant = env.CLIENT_GALLERY_GRANTS_JSON ? JSON.parse(env.CLIENT_GALLERY_GRANTS_JSON)[user.id] : null;
        if (!grant || grant.reference !== reference || !grant.accessToken) { json(res, 404, { message: 'Gallery access is not configured for this client.' }); return; }
        let method = req.method, path, body = Buffer.alloc(0), key = '';
        if (galleryPage && method === 'GET') path = `/api/public/v1/galleries/${reference}${url.search}`;
        else if (galleryItem && method === 'GET') path = `/api/public/v1/galleries/${reference}/items/${galleryItem[2]}/${galleryItem[3]}`;
        else if (selection && method === 'PUT') { path = `/api/public/v1/galleries/${reference}/selections/${selection[2]}`; body = await readBody(req); }
        else if (submit && method === 'POST') { path = `/api/public/v1/galleries/${reference}/selections/submit`; body = await readBody(req); key = String(req.headers['idempotency-key'] || ''); }
        else { json(res, 405, { message: 'Method not allowed.' }); return; }
        const upstream = await studioRequest(env, method, path, body, key, grant.accessToken);
        res.writeHead(upstream.status, { 'Content-Type': upstream.headers.get('content-type') || 'application/json', 'Cache-Control': 'private, no-store' }); res.end(Buffer.from(await upstream.arrayBuffer())); return;
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
  server.listen(Number(process.env.PORT || 8080), '0.0.0.0', () => console.log(`Aegis Studio Public Site listening on ${process.env.PORT || 8080}`));
}

