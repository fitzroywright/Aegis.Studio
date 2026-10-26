import { test } from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, sign } from 'node:crypto';
import { IntakeStore } from './intake-store.mjs';
import { ClientAuthentication } from './client-auth.mjs';

const fakeResponse=()=>({
  status:0,headers:null,
  writeHead(status,headers){this.status=status;this.headers=headers;return this;},
  end(){return this;}
});
test('validated Yahoo OIDC callback links only its verified provider subject to Studio Client ID',async()=>{
  const store=new IntakeStore(':memory:');
  const {privateKey,publicKey}=generateKeyPairSync('rsa',{modulusLength:2048});
  const jwk={...publicKey.export({format:'jwk'}),kid:'test-key',alg:'RS256',use:'sig'};
  const originalFetch=globalThis.fetch;
  let expectedNonce=null;
  const env={YAHOO_CLIENT_ID:'test-yahoo-client',YAHOO_CLIENT_SECRET:'confidential-client-secret',
    PUBLIC_WEB_ORIGIN:'http://localhost:8080'};
  const auth=new ClientAuthentication(env,store);
  function token(nonce){
    const now=Math.floor(Date.now()/1000),header={alg:'RS256',kid:'test-key',typ:'JWT'};
    const payload={iss:'https://api.login.yahoo.com',aud:'test-yahoo-client',sub:'yahoo-person-42',name:'Jane Brown',
      email:'jane@example.com',nonce,iat:now,exp:now+900};
    const a=Buffer.from(JSON.stringify(header)).toString('base64url'),b=Buffer.from(JSON.stringify(payload)).toString('base64url');
    return a+'.'+b+'.'+sign('sha256',Buffer.from(a+'.'+b),privateKey).toString('base64url');
  }
  globalThis.fetch=async (url,options)=>{
    const target=String(url);
    if(target.includes('/.well-known/openid-configuration'))return {ok:true,json:async()=>({
      issuer:'https://api.login.yahoo.com',
      authorization_endpoint:'https://api.login.yahoo.com/oauth2/request_auth',
      token_endpoint:'https://api.login.yahoo.com/oauth2/get_token',
      jwks_uri:'https://api.login.yahoo.com/openid/v1/certs'
    })};
    if(target.endsWith('/openid/v1/certs'))return {ok:true,json:async()=>({keys:[jwk]})};
    if(target.endsWith('/oauth2/get_token')){
      const params=new URLSearchParams(options.body);
      assert.equal(params.get('code_verifier')?.length>42,true);
      return {ok:true,json:async()=>({id_token:token(expectedNonce)})};
    }
    throw Error('Unexpected external fetch: '+target);
  };
  try{
    const start=fakeResponse();await auth.begin('yahoo',start);
    assert.equal(start.status,302);
    const redirected=new URL(start.headers.Location);
    expectedNonce=redirected.searchParams.get('nonce');
    const state=redirected.searchParams.get('state');
    assert.ok(state&&expectedNonce);
    const callback=fakeResponse(),url=new URL('http://localhost:8080/auth/yahoo/callback?code=valid&state='+encodeURIComponent(state));
    await assert.rejects(()=>auth.finish('yahoo',url,fakeResponse(),{headers:{cookie:'bryans_auth_state=other-state'}}),/state/);
    await auth.finish('yahoo',url,callback,{headers:{cookie:'bryans_auth_state='+state}});
    assert.equal(callback.status,302);
    const sessionCookie=callback.headers['Set-Cookie'][1].split(';')[0];
    const client=auth.current({headers:{cookie:sessionCookie}});
    assert.equal(client.provider,'yahoo');assert.equal(client.clientId,null);
    const studioClientId='22222222-2222-4222-8222-222222222222';
    const proof=store.createClientLinkChallenge(studioClientId);
    store.redeemClientLinkChallenge(proof.code,client.provider,client.subject);
    assert.equal(auth.current({headers:{cookie:sessionCookie}}).clientId,studioClientId);
    assert.throws(()=>store.redeemClientLinkChallenge(proof.code,client.provider,client.subject),/proof/i);
    const logout=fakeResponse();auth.logout({headers:{cookie:sessionCookie}},logout);
    assert.equal(auth.current({headers:{cookie:sessionCookie}}),null);
  }finally{globalThis.fetch=originalFetch;store.close();}
});

test('Microsoft common authority accepts personal and external tenants but isolates identities and rejects wrong signing-key issuer',async()=>{
  const store=new IntakeStore(':memory:');
  const {privateKey,publicKey}=generateKeyPairSync('rsa',{modulusLength:2048});
  const personal='9188040d-6c67-4c5b-b112-36a304b66dad';
  const companyA='11111111-1111-4111-8111-111111111111';
  const companyB='33333333-3333-4333-8333-333333333333';
  const issuer=tid=>'https://login.microsoftonline.com/'+tid+'/v2.0';
  const template='https://login.microsoftonline.com/{tenantid}/v2.0';
  const jwk={...publicKey.export({format:'jwk'}),kid:'ms-test-key',alg:'RS256',use:'sig',issuer:template};
  const auth=new ClientAuthentication({MICROSOFT_CLIENT_ID:'bryans-client',
    MICROSOFT_CLIENT_SECRET:'fake-secret',MICROSOFT_TENANT_ID:'common',PUBLIC_WEB_ORIGIN:'http://localhost:8080'},store);
  const originalFetch=globalThis.fetch;
  let tid=personal, keyIssuer=template, badIssuer=false, activeNonce;
  function token(nonce){
    const now=Math.floor(Date.now()/1000);
    const head=Buffer.from(JSON.stringify({alg:'RS256',kid:'ms-test-key'})).toString('base64url');
    const claims={iss:badIssuer?issuer(tid===companyB?companyA:companyB):issuer(tid),tid,aud:'bryans-client',
      sub:'same-subject',name:'Example client',nonce,iat:now,exp:now+600};
    const body=Buffer.from(JSON.stringify(claims)).toString('base64url');
    return head+'.'+body+'.'+sign('sha256',Buffer.from(head+'.'+body),privateKey).toString('base64url');
  }
  globalThis.fetch=async(url,options)=>{
    const target=String(url);
    if(target.endsWith('/.well-known/openid-configuration'))return {ok:true,json:async()=>({
      issuer:template,authorization_endpoint:'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
      token_endpoint:'https://login.microsoftonline.com/common/oauth2/v2.0/token',
      jwks_uri:'https://login.microsoftonline.com/common/discovery/v2.0/keys'})};
    if(target.endsWith('/discovery/v2.0/keys'))return {ok:true,json:async()=>({keys:[{...jwk,issuer:keyIssuer}]})};
    if(target.endsWith('/oauth2/v2.0/token'))return {ok:true,json:async()=>({
      id_token:token(activeNonce)})};
    throw Error('Unexpected fetch '+target);
  };
  async function signIn(){
    auth.cache.clear();
    const started=fakeResponse();await auth.begin('microsoft',started);
    assert.equal(new URL(started.headers.Location).pathname,'/common/oauth2/v2.0/authorize');
    activeNonce=new URL(started.headers.Location).searchParams.get('nonce');
    const state=new URL(started.headers.Location).searchParams.get('state');
    const callback=fakeResponse();
    await auth.finish('microsoft',new URL('http://localhost:8080/auth/microsoft/callback?code=ok&state='+state),
      callback,{headers:{cookie:'bryans_auth_state='+state}});
    const cookie=callback.headers['Set-Cookie'][1].split(';')[0];
    return auth.current({headers:{cookie}});
  }
  try{
    const person=await signIn();assert.equal(person.subject,'same-subject');
    tid=companyA;const company=await signIn();
    assert.equal(company.subject,companyA+':same-subject');
    tid=companyB;const other=await signIn();
    assert.equal(other.subject,companyB+':same-subject');
    const proof=store.createClientLinkChallenge('22222222-2222-4222-8222-222222222222');
    store.redeemClientLinkChallenge(proof.code,'microsoft',company.subject);
    assert.equal(store.clientForIdentity('microsoft',other.subject),null);
    badIssuer=true;await assert.rejects(signIn(),/issuer/);badIssuer=false;
    keyIssuer=issuer(companyB);tid=companyA;
    await assert.rejects(signIn(),/signing key/);
  }finally{globalThis.fetch=originalFetch;store.close();}
});
