import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, sign } from './server.mjs';

async function fixture(t, extra = {}) {
  const env = { SESSION_SECRET: 'test-only-session-secret-at-least-32-chars', PUBLIC_DEMO_MODE: 'true', ...extra };
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
test('live booking proxy signs Studio V1 request server-side and preserves idempotency key', async t => {
  const origin = await fixture(t, { PUBLIC_DEMO_MODE: 'false', STUDIO_BASE_URL: 'https://studio.stub', STUDIO_CLIENT: 'website', STUDIO_SECRET: 'server-only-secret-value-0123456789012345' });
  const originalFetch = globalThis.fetch; let forwarded = 0;
  globalThis.fetch = async (url, options) => {
    if (new URL(url).host !== 'studio.stub') return originalFetch(url, options);
    forwarded++;
    const parsed = new URL(url), timestamp = options.headers['X-Studio-Timestamp'], nonce = options.headers['X-Studio-Nonce'];
    assert.equal(options.method, 'POST');
    assert.equal(options.headers['X-Studio-Client'], 'website');
    assert.equal(options.headers['Idempotency-Key'], 'booking-demo-1');
    assert.equal(options.headers['X-Studio-Signature'], sign('server-only-secret-value-0123456789012345', 'POST', parsed.pathname + parsed.search, timestamp, nonce, options.body, 'booking-demo-1'));
    return new Response(JSON.stringify({ reference: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', status: 'received', requiredDeposit: 1000, currency: 'JMD' }), { status: 201, headers: { 'Content-Type': 'application/json' } });
  };
  t.after(() => { globalThis.fetch = originalFetch; });
  const body = JSON.stringify({ clientName: 'Demo Client', clientEmail: 'client@example.test', sessionType: 'Portrait', startsAt: '2026-11-01T15:00:00Z', endsAt: '2026-11-01T16:00:00Z' });
  const response = await originalFetch(`${origin}/api/bookings`, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json', 'Idempotency-Key': 'booking-demo-1' }, body });
  assert.equal(response.status, 201);
  assert.equal(forwarded, 1);
  assert.doesNotMatch(await response.text(), /server-only-secret/);
});
test('serves standalone client assets and an operational health endpoint', async t => {
  const origin = await fixture(t);
  assert.equal((await fetch(`${origin}/health`)).status, 200);
  assert.match(await (await fetch(origin)).text(), /Client Galleries/);
  assert.equal((await fetch(`${origin}/styles.css`)).headers.get('content-type'), 'text/css; charset=utf-8');
  assert.equal((await fetch(`${origin}/app.js`)).headers.get('content-type'), 'text/javascript; charset=utf-8');
});
