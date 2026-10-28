import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { IntakeStore } from './intake-store.mjs';
import { ClientAuthentication } from './client-auth.mjs';

const origin='https://web.bryanstudiosltd.com';
const metadata={issuer:'https://accounts.google.com',
  authorization_endpoint:'https://accounts.google.com/o/oauth2/v2/auth',
  token_endpoint:'https://oauth2.googleapis.com/token',jwks_uri:'https://www.googleapis.com/oauth2/v3/certs'};
const response=()=>({headers:null,writeHead(status,headers){this.status=status;this.headers=headers;return this;},end(){return this;}});
const keys=generateKeyPairSync('rsa',{modulusLength:2048});
const wrongKeys=generateKeyPairSync('rsa',{modulusLength:2048});
const jwk={...keys.publicKey.export({format:'jwk'}),kid:'google-test-key',alg:'RS256',use:'sig'};

function fixture(t){
  const store=new IntakeStore(':memory:');
  const env={NODE_ENV:'production',PUBLIC_WEB_ORIGIN:origin,
    GOOGLE_CLIENT_ID:'test.apps.googleusercontent.com',GOOGLE_CLIENT_SECRET:'test-secret'};
  const auth=new ClientAuthentication(env,store);
  const originalFetch=globalThis.fetch;
  const f={store,auth,env,overrides:{},metadata:{...metadata},key:keys.privateKey,fetches:[]};
  globalThis.fetch=async(url,options={})=>{
    const target=String(url);f.fetches.push(target);
    assert.equal(options.redirect,'error');
    if(target===auth.config.google.discovery)return {ok:true,json:async()=>f.metadata};
    if(target===metadata.jwks_uri)return {ok:true,json:async()=>({keys:[jwk]})};
    if(target===metadata.token_endpoint){
      const body=new URLSearchParams(options.body);
      assert.equal(options.method,'POST');assert.equal(body.get('grant_type'),'authorization_code');
      assert.equal(body.get('client_id'),env.GOOGLE_CLIENT_ID);assert.equal(body.get('client_secret'),env.GOOGLE_CLIENT_SECRET);
      assert.equal(body.get('redirect_uri'),origin+'/auth/google/callback');
      assert.equal(createHash('sha256').update(body.get('code_verifier')).digest('base64url'),f.challenge);
      const now=Math.floor(Date.now()/1000);
      const claims={iss:metadata.issuer,aud:env.GOOGLE_CLIENT_ID,sub:'google-subject',
        name:'Gallery client',email:'client@gmail.com',email_verified:true,
        iat:now,exp:now+600,nonce:f.nonce,...f.overrides};
      const header=Buffer.from(JSON.stringify({alg:'RS256',kid:'google-test-key'})).toString('base64url');
      const payload=Buffer.from(JSON.stringify(claims)).toString('base64url');
      const content=header+'.'+payload;
      const token=content+'.'+sign('sha256',Buffer.from(content),f.key).toString('base64url');
      return {ok:true,json:async()=>({id_token:token})};
    }
    throw Error('Unexpected external request: '+target);
  };
  t.after(()=>{globalThis.fetch=originalFetch;store.close();});
  f.start=async()=>{
    const res=response();
    assert.equal(await auth.handle({method:'GET',headers:{}},res,new URL(origin+'/auth/google'),
      'web.bryanstudiosltd.com',()=>assert.fail('Unexpected JSON response')),true);
    const target=new URL(res.headers.Location);
    assert.equal(target.origin+target.pathname,metadata.authorization_endpoint);
    assert.equal(target.searchParams.get('scope'),'openid profile email');
    assert.equal(target.searchParams.get('response_type'),'code');
    assert.equal(target.searchParams.get('code_challenge_method'),'S256');
    assert.match(res.headers['Set-Cookie'],/HttpOnly; SameSite=Lax; Max-Age=600; Secure/);
    f.state=target.searchParams.get('state');f.nonce=target.searchParams.get('nonce');
    f.challenge=target.searchParams.get('code_challenge');
    return target;
  };
  f.finish=async(cookie='bryans_auth_state='+f.state)=>{
    const res=response();
    await auth.handle({method:'GET',headers:{cookie}},res,
      new URL(origin+'/auth/google/callback?code=valid-code&state='+f.state),'web.bryanstudiosltd.com',()=>{});
    return res;
  };
  return f;
}

test('Google sign-in uses verified subject, explicit one-time linking, isolated identities and logout',async t=>{
  const f=fixture(t);
  assert.deepEqual(f.auth.providers(),['google']);
  await f.start();
  const res=await f.finish();
  assert.equal(res.headers.Location,origin+'/#galleries');
  assert.equal(res.headers['Cache-Control'],'no-store');
  assert.match(res.headers['Set-Cookie'][1],/HttpOnly; SameSite=Lax; Max-Age=28800; Secure/);
  const req={headers:{cookie:res.headers['Set-Cookie'][1].split(';')[0]}};
  const user=f.auth.current(req);
  assert.equal(user.provider,'google');assert.equal(user.subject,'google-subject');
  assert.equal(user.email,'client@gmail.com');assert.equal(user.clientId,null);
  const client='22222222-2222-4222-8222-222222222222';
  const proof=f.store.createClientLinkChallenge(client);
  f.store.redeemClientLinkChallenge(proof.code,user.provider,user.subject);
  assert.equal(f.auth.current(req).clientId,client);
  assert.equal(f.store.clientForIdentity('microsoft',user.subject),null);
  assert.throws(()=>f.store.redeemClientLinkChallenge(proof.code,user.provider,user.subject),/proof/i);
  const conflict=f.store.createClientLinkChallenge('33333333-3333-4333-8333-333333333333');
  assert.throws(()=>f.store.redeemClientLinkChallenge(conflict.code,user.provider,user.subject),/another client/);
  await assert.rejects(()=>f.finish(),/expired or invalid/);
  // Identical email does not bind a different Google subject to this client.
  f.overrides={sub:'another-google-subject'};await f.start();
  const other=await f.finish();
  assert.equal(f.auth.current({headers:{cookie:other.headers['Set-Cookie'][1].split(';')[0]}}).clientId,null);
  f.auth.logout(req,response());assert.equal(f.auth.current(req),null);
});

test('Google rejects invalid identity tokens without creating a session',async t=>{
  const cases=[
    ['wrong audience',{aud:'another-client'}],['wrong issuer',{iss:'https://attacker.example'}],
    ['wrong nonce',{nonce:'another-nonce'}],['expired',{exp:1}],['missing expiry',{exp:undefined}],
    ['missing issued time',{iat:undefined}],['future issued time',{iat:Math.floor(Date.now()/1000)+3600}],
    ['wrong authorized party',{azp:'another-client'}],['non-string subject',{sub:42}],['bad signature',{}]
  ];
  for(const [name,overrides] of cases)await t.test(name,async t=>{
    const f=fixture(t);f.overrides=overrides;if(name==='bad signature')f.key=wrongKeys.privateKey;
    await f.start();await assert.rejects(()=>f.finish(),/identity/);
    assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM client_auth_sessions').get().n,0);
  });
});

test('Google supports both documented issuer forms and omits unverified email',async t=>{
  const f=fixture(t);f.overrides={iss:'accounts.google.com',email_verified:false,azp:f.env.GOOGLE_CLIENT_ID};
  await f.start();const res=await f.finish();
  const user=f.auth.current({headers:{cookie:res.headers['Set-Cookie'][1].split(';')[0]}});
  assert.equal(user.subject,'google-subject');assert.equal(user.email,null);
});

test('Google rejects state mismatch, denied authorization, wrong host and wrong method',async t=>{
  const f=fixture(t);await f.start();
  await assert.rejects(()=>f.finish('bryans_auth_state=wrong'),/state/);
  await assert.rejects(()=>f.auth.handle({method:'GET',headers:{cookie:'bryans_auth_state='+f.state}},response(),
    new URL(origin+'/auth/google/callback?error=access_denied&state='+f.state),'web.bryanstudiosltd.com',()=>{}),/state/);
  await assert.rejects(()=>f.auth.handle({method:'GET'},response(),new URL(origin+'/auth/google'),
    'api.bryanstudiosltd.com',()=>{}),/web hostname/);
  await assert.rejects(()=>f.auth.handle({method:'POST'},response(),new URL(origin+'/auth/google'),
    'web.bryanstudiosltd.com',()=>{}),/Method/);
  assert.equal(f.fetches.includes(metadata.token_endpoint),false);
});

test('Google stays disabled until both credentials exist',async t=>{
  const f=fixture(t);
  for(const env of [{},{GOOGLE_CLIENT_ID:'only-id'},{GOOGLE_CLIENT_SECRET:'only-secret'}]){
    const auth=new ClientAuthentication({...env,PUBLIC_WEB_ORIGIN:origin},f.store);
    assert.deepEqual(auth.providers(),[]);
    await assert.rejects(()=>auth.begin('google',response()),/not been configured/);
  }
});

test('Google discovery cannot redirect tokens or key requests to untrusted endpoints',async t=>{
  for(const field of ['authorization_endpoint','token_endpoint','jwks_uri','issuer'])await t.test(field,async t=>{
    const f=fixture(t);f.metadata[field]='https://attacker.example/';
    await assert.rejects(()=>f.start(),/Untrusted/);
    assert.deepEqual(f.fetches,[f.auth.config.google.discovery]);
  });
});
