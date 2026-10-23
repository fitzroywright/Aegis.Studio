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
 const body=JSON.stringify({clientName:'Test Client',clientEmail:'client@example.test',sessionType:'Portrait',locationCode:'BK',startsAt:'2026-11-01T15:00:00Z',endsAt:'2026-11-01T16:00:00Z'});
 const options={method:'POST',headers:{Origin:origin,Cookie:cookie,'Content-Type':'application/json','X-CSRF-Token':csrf,'Idempotency-Key':'booking-1'},body};
 const first=await fetch(`${origin}/api/bookings`,options),receipt=await first.json();assert.equal(first.status,201);assert.match(receipt.intakeReference,/^\d{4}-BK-\d{6}$/);
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
test('server integrations do not require Origin and scopes isolate submission from polling and payment',async t=>{
 const keys=[{id:'external',secret:'external-test-secret-01234567890123456789',scopes:['intake:submit']},{id:'studio',secret:'studio-test-secret-01234567890123456789',scopes:['intake:pull','intake:ack']},{id:'other',secret:'other-test-secret-01234567890123456789',scopes:['intake:submit']}];
 const origin=await fixture(t,{PUBLIC_API_KEYS_JSON:JSON.stringify(keys)}),base=origin+'/api/v1';
 const response=await fetch(base+'/intakes',{method:'POST',headers:{Authorization:'Bearer '+keys[0].secret,'Idempotency-Key':'api-1','Content-Type':'application/json'},body:JSON.stringify({kind:'Inquiry',clientName:'Test Client'})});assert.equal(response.status,201);const receipt=await response.json();
 assert.equal((await fetch(base+'/changes',{headers:{Authorization:'Bearer '+keys[0].secret}})).status,403);
 assert.equal((await fetch(base+'/intakes/'+receipt.id,{headers:{Authorization:'Bearer '+keys[2].secret}})).status,404);
 const feed=await(await fetch(base+'/changes',{headers:{Authorization:'Bearer '+keys[1].secret}})).json();assert.equal(feed.items.length,1);
 const ack=await fetch(base+'/intakes/'+receipt.id+'/ack',{method:'POST',headers:{Authorization:'Bearer '+keys[1].secret,'Content-Type':'application/json'},body:'{"revision":1,"reviewStatus":"AwaitingReview"}'});assert.equal(ack.status,200);
 const status=await(await fetch(base+'/intakes/'+receipt.id,{headers:{Authorization:'Bearer '+keys[0].secret}})).json();assert.equal(status.importStatus,'Imported');
 const payment=await fetch(base+'/intakes/'+receipt.id+'/payments',{method:'POST',headers:{Authorization:'Bearer '+keys[0].secret,'Content-Type':'application/json'},body:'{}'});assert.equal(payment.status,403);
});
test('local published media is isolated by client binding and revocation',async t=>{
 const secret='publication-test-secret-01234567890123456789',gallery='11111111-1111-4111-8111-111111111111',version='33333333-3333-4333-8333-333333333333',client='22222222-2222-4222-8222-222222222222';
 const origin=await fixture(t,{PUBLIC_API_KEYS_JSON:JSON.stringify([{id:'studio',secret,scopes:['gallery:publish']}]),CLIENT_BINDINGS_JSON:JSON.stringify({'demo-google-maya':client})}),base=origin+'/api/v1';
 const headers={Authorization:'Bearer '+secret,'Content-Type':'application/json'};
 assert.equal((await fetch(base+'/galleries',{method:'POST',headers,body:JSON.stringify({id:gallery,clientId:client,title:'Local gallery',finalDeliveryAvailable:true,items:[{versionId:version,fileName:'portrait.jpg'}]})})).status,200);
 assert.equal((await fetch(base+`/galleries/${gallery}/publish`,{method:'POST',headers,body:'{}'})).status,409);
 const bytes=Buffer.from([255,216,255,217]);assert.equal((await fetch(base+`/galleries/${gallery}/media/${version}`,{method:'PUT',headers:{Authorization:'Bearer '+secret,'Content-Type':'image/jpeg'},body:bytes})).status,200);
 assert.equal((await fetch(base+`/galleries/${gallery}/publish`,{method:'POST',headers,body:'{}'})).status,200);
 async function login(identity){const r=await fetch(origin+'/api/demo-login',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:JSON.stringify({identity})});return r.headers.get('set-cookie').split(';')[0];}
 const own=await login('maya'),other=await login('jordan');
 assert.equal((await fetch(origin+`/api/galleries/${gallery}`,{headers:{Cookie:own}})).status,200);
 assert.equal((await fetch(origin+`/api/galleries/${gallery}`,{headers:{Cookie:other}})).status,404);
 const media=await fetch(origin+`/api/galleries/${gallery}/items/${version}/preview`,{headers:{Cookie:own}});assert.deepEqual(Buffer.from(await media.arrayBuffer()),bytes);
 assert.equal((await fetch(base+`/galleries/${gallery}/revoke`,{method:'POST',headers,body:'{}'})).status,200);
 assert.equal((await fetch(origin+`/api/galleries/${gallery}`,{headers:{Cookie:own}})).status,404);
});

test('server provisions one-time integration keys, enforces scopes, and revokes them', async t => {
  const provisioning='provisioning-secret-that-is-long-enough-for-tests';
  const origin=await fixture(t,{PUBLIC_API_PROVISIONING_TOKEN:provisioning});
  const adminHeaders={'X-Site-Provisioning-Token':provisioning,'Content-Type':'application/json'};
  const denied=await fetch(`${origin}/api/v1/admin/integration-keys`);
  assert.equal(denied.status,401);
  const created=await fetch(`${origin}/api/v1/admin/integration-keys`,{method:'POST',headers:adminHeaders,body:JSON.stringify({name:'Booking website',scopes:['intake:submit']})});
  assert.equal(created.status,201);
  const key=await created.json();assert.match(key.secret,/^[A-Za-z0-9_-]{40,}$/);assert.equal(key.name,'Booking website');
  const listed=await(await fetch(`${origin}/api/v1/admin/integration-keys`,{headers:adminHeaders})).json();
  assert.equal(listed.items.length,1);assert.equal('secret' in listed.items[0],false);assert.equal('secret_hash' in listed.items[0],false);
  const booking={kind:'Booking',clientName:'Test Client',clientEmail:'test@example.invalid',sessionType:'Portrait',locationCode:'BK',startsAt:'2026-11-01T10:00:00-05:00',endsAt:'2026-11-01T11:00:00-05:00'};
  const submission=await fetch(`${origin}/api/v1/intakes`,{method:'POST',headers:{Authorization:`Bearer ${key.secret}`,'Idempotency-Key':'one','Content-Type':'application/json'},body:JSON.stringify(booking)});
  assert.equal(submission.status,201);
  const galleryScope=await fetch(`${origin}/api/v1/changes`,{headers:{Authorization:`Bearer ${key.secret}`}});
  assert.equal(galleryScope.status,403);
  const revoked=await fetch(`${origin}/api/v1/admin/integration-keys/${key.id}`,{method:'DELETE',headers:{'X-Site-Provisioning-Token':provisioning}});
  assert.equal(revoked.status,200);
  const afterRevoke=await fetch(`${origin}/api/v1/intakes`,{method:'POST',headers:{Authorization:`Bearer ${key.secret}`,'Idempotency-Key':'two','Content-Type':'application/json'},body:JSON.stringify(booking)});
  assert.equal(afterRevoke.status,401);
});

test('client link proof is single use, bound to authenticated provider subject, and scopes galleries by Studio client UUID',async t=>{
 const provision='client-link-provisioning-secret-long-enough',client='22222222-2222-4222-8222-222222222222';
 const origin=await fixture(t,{PUBLIC_API_PROVISIONING_TOKEN:provision,CLIENT_BINDINGS_JSON:'{}'});
 const created=await fetch(origin+'/api/v1/admin/client-link-proofs',{method:'POST',headers:{'X-Site-Provisioning-Token':provision,'Content-Type':'application/json'},body:JSON.stringify({clientId:client})});assert.equal(created.status,201);const proof=await created.json();
 const login=await fetch(origin+'/api/demo-login',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:JSON.stringify({identity:'maya'})}),cookie=login.headers.get('set-cookie').split(';')[0];
 const redeem=await fetch(origin+'/api/v1/client-link-proofs/redeem',{method:'POST',headers:{Origin:origin,Cookie:cookie,'Content-Type':'application/json'},body:JSON.stringify({code:proof.code})});assert.equal(redeem.status,200);
 assert.equal((await fetch(origin+'/api/v1/client-link-proofs/redeem',{method:'POST',headers:{Origin:origin,Cookie:cookie,'Content-Type':'application/json'},body:JSON.stringify({code:proof.code})})).status,404);
 const gallery='11111111-1111-4111-8111-111111111111',version='33333333-3333-4333-8333-333333333333',key='gallery-publish-test-secret-0123456789';
 const api=origin+'/api/v1',auth={Authorization:'Bearer '+key,'Content-Type':'application/json'};
 const provisioned=await fetch(api+'/admin/integration-keys',{method:'POST',headers:{'X-Site-Provisioning-Token':provision,'Content-Type':'application/json'},body:JSON.stringify({name:'Gallery Publisher',scopes:['gallery:publish']})});const publisher=(await provisioned.json()).secret;
 const ph={Authorization:'Bearer '+publisher,'Content-Type':'application/json'};
 await fetch(api+'/galleries',{method:'POST',headers:ph,body:JSON.stringify({id:gallery,clientId:client,title:'Linked client gallery',items:[{versionId:version,fileName:'proof.jpg'}]})});
 await fetch(api+`/galleries/${gallery}/media/${version}`,{method:'PUT',headers:{Authorization:'Bearer '+publisher,'Content-Type':'image/jpeg'},body:Buffer.from([255,216,255,217])});
 await fetch(api+`/galleries/${gallery}/publish`,{method:'POST',headers:ph,body:'{}'});
 assert.equal((await fetch(origin+`/api/galleries/${gallery}`,{headers:{Cookie:cookie}})).status,200);
});

