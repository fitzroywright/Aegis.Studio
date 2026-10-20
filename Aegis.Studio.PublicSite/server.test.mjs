import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, sign } from './server.mjs';

async function fixture(t, extra = {}) {
  const env = { SESSION_SECRET: 'test-only-session-secret-at-least-32-chars', PUBLIC_DEMO_MODE: 'true', INTAKE_DB_PATH: ':memory:', ...extra };
  const server = createServer(env);
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  t.after(() => new Promise(resolve => server.close(resolve)));
  return `http://127.0.0.1:${server.address().port}`;
}
test('signing binds method, path, exact body and idempotency key', () => {
  const body = Buffer.from('{"ok":true}');
  const expected = sign('secret', 'POST', '/api/public/v1/bookings', '1', 'nonce', body, 'booking-1');
  assert.equal(expected.length, 64);
  assert.notEqual(expected, sign('secret', 'POST', '/api/public/v1/bookings', '1', 'nonce', Buffer.from('{"ok":false}'), 'booking-1'));
  assert.notEqual(expected, sign('secret', 'POST', '/api/public/v1/bookings', '1', 'nonce', body, 'booking-2'));
});
test('demo account can open only its own gallery, even when a second reference is guessed', async t => {
  const origin = await fixture(t);
  const login = await fetch(`${origin}/api/demo-login`, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json' }, body: JSON.stringify({ identity: 'maya' }) });
  assert.equal(login.status, 200);
  const cookie = login.headers.get('set-cookie').split(';')[0];
  const own = await fetch(`${origin}/api/galleries/10000000-0000-4000-8000-000000000001`, { headers: { Cookie: cookie } });
  assert.equal(own.status, 200);
  assert.equal((await own.json()).title, 'Maya & Daniel · Engagement');
  const other = await fetch(`${origin}/api/galleries/20000000-0000-4000-8000-000000000002`, { headers: { Cookie: cookie } });
  assert.equal(other.status, 404);
});
test('demo selection actions update and submit only the signed-in client gallery', async t => {
  const origin = await fixture(t);
  const login = await fetch(`${origin}/api/demo-login`, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json' }, body: JSON.stringify({ identity: 'jordan' }) });
  const cookie = login.headers.get('set-cookie').split(';')[0], reference = '20000000-0000-4000-8000-000000000002', version = '20000000-0000-4000-8000-000000000021';
  let response = await fetch(`${origin}/api/galleries/${reference}/selections/${version}`, { method: 'PUT', headers: { Origin: origin, Cookie: cookie, 'Content-Type': 'application/json' }, body: JSON.stringify({ selected: true }) });
  assert.equal(response.status, 200);
  response = await fetch(`${origin}/api/galleries/${reference}`, { headers: { Cookie: cookie } });
  assert.equal((await response.json()).items[0].selected, true);
  response = await fetch(`${origin}/api/galleries/${reference}/selections/submit`, { method: 'POST', headers: { Origin: origin, Cookie: cookie } });
  assert.deepEqual((await response.json()).versionIds, [version]);
});
test('demo sign-in rejects caller-chosen identities and cross-origin login', async t => {
  const origin = await fixture(t);
  let response = await fetch(`${origin}/api/demo-login`, { method: 'POST', headers: { Origin: 'https://attacker.example', 'Content-Type': 'application/json' }, body: JSON.stringify({ identity: 'jordan' }) });
  assert.equal(response.status, 403);
  response = await fetch(`${origin}/api/demo-login`, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json' }, body: JSON.stringify({ identity: 'admin' }) });
  assert.equal(response.status, 400);
});
test('live mode disables fake social sign-in and rejects non-HTTPS Studio upstream', async t => {
  const origin = await fixture(t, { PUBLIC_DEMO_MODE: 'false', STUDIO_BASE_URL: 'http://studio.example', STUDIO_CLIENT: 'website', STUDIO_SECRET: 'server-side-secret' });
  const response = await fetch(`${origin}/api/demo-login`, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json' }, body: JSON.stringify({ identity: 'maya' }) });
  assert.equal(response.status, 404);
});
test('public booking is durably saved without calling internal Studio and replays safely',async t=>{
 const origin=await fixture(t,{PUBLIC_DEMO_MODE:'false'}),session=await fetch(`${origin}/api/intake-session`),csrf=(await session.json()).csrf,cookie=session.headers.get('set-cookie').split(';')[0];
 const body=JSON.stringify({clientName:'Test Client',clientEmail:'client@example.test',sessionType:'Portrait',startsAt:'2026-11-01T15:00:00Z',endsAt:'2026-11-01T16:00:00Z'});
 const options={method:'POST',headers:{Origin:origin,Cookie:cookie,'Content-Type':'application/json','X-CSRF-Token':csrf,'Idempotency-Key':'booking-1'},body};
 const first=await fetch(`${origin}/api/bookings`,options),receipt=await first.json();assert.equal(first.status,201);assert.match(receipt.intakeReference,/^\d{4}-W-\d{6}$/);
 const replay=await(await fetch(`${origin}/api/bookings`,options)).json();assert.equal(receipt.id,replay.id);assert.equal(receipt.accessToken,replay.accessToken);
 const noAccess=await fetch(`${origin}/api/bookings/${receipt.id}`);assert.equal(noAccess.status,404);
 const status=await(await fetch(`${origin}/api/bookings/${receipt.id}`,{headers:{'X-Intake-Token':receipt.accessToken}})).json();assert.equal(status.importStatus,'Pending');
 const conflict=await fetch(`${origin}/api/bookings`,{...options,body:body.replace('Test Client','Other Client')});assert.equal(conflict.status,409);
});
test('serves standalone client assets and an operational health endpoint', async t => {
  const origin = await fixture(t);
  assert.equal((await fetch(`${origin}/health`)).status, 200);
  assert.match(await (await fetch(origin)).text(), /Client Galleries/);
  assert.equal((await fetch(`${origin}/styles.css`)).headers.get('content-type'), 'text/css; charset=utf-8');
  assert.equal((await fetch(`${origin}/app.js`)).headers.get('content-type'), 'text/javascript; charset=utf-8');
});
