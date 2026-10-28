import { createHash, createPublicKey, randomBytes, timingSafeEqual, verify } from 'node:crypto';
const digest=x=>createHash('sha256').update(x).digest('hex');
const rand=()=>randomBytes(32).toString('base64url');
const equal=(a,b)=>{const x=Buffer.from(String(a||'')),y=Buffer.from(String(b||''));return x.length===y.length&&timingSafeEqual(x,y);};
const reject=(status,message)=>Object.assign(new Error(message),{status});
const TENANT_GUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MICROSOFT_CONSUMER_TENANT='9188040d-6c67-4c5b-b112-36a304b66dad';
async function requestJson(url,options={}){
  const r=await fetch(url,{...options,redirect:'error',signal:AbortSignal.timeout(12000)});
  if(!r.ok)throw reject(502,'Identity provider request failed.');
  return r.json();
}
function verifyToken(token,metadata,keys,clientId,nonce,provider,msTenant){
  const parts=String(token||'').split('.');if(parts.length!==3)throw reject(401,'Invalid identity token.');
  let head,claims;
  try{head=JSON.parse(Buffer.from(parts[0],'base64url'));claims=JSON.parse(Buffer.from(parts[1],'base64url'));}
  catch{throw reject(401,'Invalid identity token.');}
  if(head.alg!=='RS256'&&head.alg!=='ES256')throw reject(401,'Unsupported identity signature.');
  const jwk=keys.keys.find(k=>k.kid===head.kid&&(!k.alg||k.alg===head.alg)&&k.use!=='enc');
  if(!jwk)throw reject(401,'Unknown identity signing key.');
  const key=createPublicKey({key:jwk,format:'jwk'});
  const params=head.alg==='ES256'?{key,dsaEncoding:'ieee-p1363'}:key;
  if(!verify('sha256',Buffer.from(parts[0]+'.'+parts[1]),params,Buffer.from(parts[2],'base64url')))
    throw reject(401,'Invalid identity signature.');
  const now=Math.floor(Date.now()/1000);
  if(provider==='google'&&(typeof claims.sub!=='string'||!claims.sub||
      !Number.isFinite(claims.exp)||!Number.isFinite(claims.iat)||
      (claims.azp!==undefined&&claims.azp!==clientId)))
    throw reject(401,'Invalid Google identity claims.');
  if(!claims.sub||claims.aud!==clientId||claims.exp<=now||claims.iat>now+60||!equal(claims.nonce,nonce))
    throw reject(401,'Invalid identity claims.');
  if(provider==='microsoft'){
    const tenant=claims.tid;
    if(typeof tenant!=='string'||!TENANT_GUID.test(tenant))
      throw reject(401,'Invalid Microsoft tenant ID.');
    if(msTenant==='consumers'&&tenant!==MICROSOFT_CONSUMER_TENANT
       || msTenant!=='common'&&msTenant!=='consumers'&&tenant!==msTenant)
      throw reject(401,'Microsoft identity is not issued by the configured tenant.');
    const issuer='https://login.microsoftonline.com/'+tenant+'/v2.0';
    if(claims.iss!==issuer)throw reject(401,'Invalid Microsoft identity issuer.');
    const expectedMetadataIssuer=msTenant==='common'
      ?'https://login.microsoftonline.com/{tenantid}/v2.0'
      :issuer;
    if(metadata.issuer!==expectedMetadataIssuer)
      throw reject(401,'Unexpected Microsoft discovery issuer.');
    if(typeof jwk.issuer!=='string') {
      if(msTenant==='common')throw reject(401,'Missing Microsoft signing-key issuer.');
    }else {
      const signingKeyIssuer=jwk.issuer.replaceAll('{tenantid}',tenant);
      if(signingKeyIssuer!==issuer)throw reject(401,'Microsoft signing key does not belong to this tenant.');
    }
    // Microsoft sub is tenant-scoped. Include tid to prevent linking identities across tenants.
    // Keep the legacy personal-account subject for existing single-consumer deployments.
    return {...claims,verifiedSubject:msTenant==='common'&&tenant!==MICROSOFT_CONSUMER_TENANT?tenant+':'+claims.sub:claims.sub};
  }else if(provider==='google'){
    if(metadata.issuer!=='https://accounts.google.com'||
       !['https://accounts.google.com','accounts.google.com'].includes(claims.iss))
      throw reject(401,'Invalid Google identity issuer.');
  }else if(claims.iss!==metadata.issuer)throw reject(401,'Invalid identity issuer.');
  return claims;
}
export class ClientAuthentication {
  constructor(env,store){
    this.env=env;this.store=store;this.secure=env.NODE_ENV==='production';
    this.origin=String(env.PUBLIC_WEB_ORIGIN||'http://localhost:8080');
    const u=new URL(this.origin);
    if(u.pathname!=='/'||u.search||u.hash||(this.secure&&u.protocol!=='https:'))
      throw Error('PUBLIC_WEB_ORIGIN must be an origin and HTTPS in production.');
    this.host=u.hostname.toLowerCase();
    this.msTenant=String(env.MICROSOFT_TENANT_ID||'consumers').toLowerCase();
    if(this.msTenant!=='common'&&this.msTenant!=='consumers'&&!TENANT_GUID.test(this.msTenant))
      throw Error('MICROSOFT_TENANT_ID must be common, consumers or a specific organization tenant UUID.');
    this.config={
      microsoft:{id:env.MICROSOFT_CLIENT_ID,secret:env.MICROSOFT_CLIENT_SECRET,
        discovery:'https://login.microsoftonline.com/'+this.msTenant+'/v2.0/.well-known/openid-configuration'},
      yahoo:{id:env.YAHOO_CLIENT_ID,secret:env.YAHOO_CLIENT_SECRET,discovery:'https://api.login.yahoo.com/.well-known/openid-configuration'},
      google:{id:env.GOOGLE_CLIENT_ID,secret:env.GOOGLE_CLIENT_SECRET,discovery:'https://accounts.google.com/.well-known/openid-configuration'},
      facebook:{id:env.FACEBOOK_APP_ID,secret:env.FACEBOOK_APP_SECRET}
    };
    store.db.exec("CREATE TABLE IF NOT EXISTS client_auth_states(state_hash TEXT PRIMARY KEY,provider TEXT NOT NULL,verifier TEXT NOT NULL,nonce TEXT NOT NULL,expires_at INTEGER NOT NULL)");
    store.db.exec("CREATE TABLE IF NOT EXISTS client_auth_sessions(token_hash TEXT PRIMARY KEY,provider TEXT NOT NULL,subject TEXT NOT NULL,name TEXT NOT NULL,email TEXT,expires_at INTEGER NOT NULL)");
    this.cache=new Map();
  }
  providers(){return Object.keys(this.config).filter(name=>this.config[name].id&&this.config[name].secret);}
  token(req){return String(req.headers.cookie||'').split(';').map(s=>s.trim()).find(s=>s.startsWith('bryans_client_session='))?.slice(22)||'';}
  current(req){
    const token=this.token(req);if(!token)return null;
    const row=this.store.db.prepare('SELECT provider,subject,name,email,expires_at FROM client_auth_sessions WHERE token_hash=?').get(digest(token));
    if(!row||row.expires_at<Date.now())return null;
    return {provider:row.provider,subject:row.subject,name:row.name,email:row.email,
      clientId:this.store.clientForIdentity(row.provider,row.subject)||null};
  }
  async discovery(provider){
    const old=this.cache.get(provider);if(old&&old.until>Date.now())return old.value;
    const metadata=await requestJson(this.config[provider].discovery);
    // Google uses distinct hosts for authorization, tokens and signing keys.
    const googleEndpoints={authorization_endpoint:'https://accounts.google.com/o/oauth2/v2/auth',
      token_endpoint:'https://oauth2.googleapis.com/token',jwks_uri:'https://www.googleapis.com/oauth2/v3/certs'};
    const host=provider==='microsoft'?'login.microsoftonline.com':'api.login.yahoo.com';
    for(const k of ['authorization_endpoint','token_endpoint','jwks_uri']){
      if(provider==='google'?metadata[k]!==googleEndpoints[k]:
         !metadata[k]||new URL(metadata[k]).hostname!==host||!metadata[k].startsWith('https://'))
        throw reject(502,'Untrusted identity provider metadata.');
    }
    if(provider==='google'&&metadata.issuer!=='https://accounts.google.com')
      throw reject(502,'Untrusted Google discovery issuer.');
    const value={metadata,keys:await requestJson(metadata.jwks_uri)};
    this.cache.set(provider,{value,until:Date.now()+3600000});return value;
  }
  async begin(provider,res){
    const config=this.config[provider];
    if(!config?.id||!config.secret)throw reject(503,'This sign-in provider has not been configured.');
    const state=rand(),verifier=rand(),nonce=rand(),callback=this.origin+'/auth/'+provider+'/callback';
    this.store.db.prepare('DELETE FROM client_auth_states WHERE expires_at<?').run(Date.now());
    this.store.db.prepare('INSERT INTO client_auth_states VALUES(?,?,?,?,?)').run(digest(state),provider,verifier,nonce,Date.now()+600000);
    const target=provider==='facebook'?new URL('https://www.facebook.com/v24.0/dialog/oauth'):
      new URL((await this.discovery(provider)).metadata.authorization_endpoint);
    if(provider==='facebook')target.searchParams.set('scope','public_profile,email');
    else {
      target.searchParams.set('scope','openid profile email');
      target.searchParams.set('nonce',nonce);
      target.searchParams.set('code_challenge',createHash('sha256').update(verifier).digest('base64url'));
      target.searchParams.set('code_challenge_method','S256');
    }
    for(const [k,v] of Object.entries({response_type:'code',client_id:config.id,redirect_uri:callback,state}))
      target.searchParams.set(k,v);
    res.writeHead(302,{Location:target.href,'Cache-Control':'no-store',
      'Set-Cookie':'bryans_auth_state='+state+'; Path=/auth/; HttpOnly; SameSite=Lax; Max-Age=600'+(this.secure?'; Secure':'')}).end();
  }
  async finish(provider,url,res,req){
    const state=url.searchParams.get('state'),code=url.searchParams.get('code');
    const stateCookie=String(req.headers.cookie||'').split(';').map(x=>x.trim())
      .find(x=>x.startsWith('bryans_auth_state='))?.slice('bryans_auth_state='.length);
    if(!state||!code||url.searchParams.has('error')||!equal(stateCookie,state))
      throw reject(401,'Authentication state is not bound to this browser.');
    const row=this.store.db.prepare('SELECT * FROM client_auth_states WHERE state_hash=?').get(digest(state));
    if(!row||row.provider!==provider||row.expires_at<Date.now())throw reject(401,'Sign-in state expired or invalid.');
    this.store.db.prepare('DELETE FROM client_auth_states WHERE state_hash=?').run(digest(state));
    const c=this.config[provider],callback=this.origin+'/auth/'+provider+'/callback';
    let identity;
    if(provider==='facebook'){
      const token=await requestJson('https://graph.facebook.com/v24.0/oauth/access_token',{
        method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},
        body:new URLSearchParams({client_id:c.id,client_secret:c.secret,redirect_uri:callback,code}).toString()
      });
      const debug=new URL('https://graph.facebook.com/debug_token');
      debug.searchParams.set('input_token',token.access_token);
      const validated=(await requestJson(debug.href,{headers:{Authorization:'Bearer '+c.id+'|'+c.secret}})).data;
      if(!validated?.is_valid||String(validated.app_id)!==String(c.id))throw reject(401,'Invalid Facebook token.');
      const person=await requestJson('https://graph.facebook.com/me?fields=id,name,email',{headers:{Authorization:'Bearer '+token.access_token}});
      if(!person.id||String(person.id)!==String(validated.user_id))throw reject(401,'Facebook identity mismatch.');
      identity={subject:String(person.id),name:String(person.name||'Client'),email:person.email||null};
    }else{
      const {metadata,keys}=await this.discovery(provider);
      const token=await requestJson(metadata.token_endpoint,{
        method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},
        body:new URLSearchParams({grant_type:'authorization_code',client_id:c.id,client_secret:c.secret,
          redirect_uri:callback,code,code_verifier:row.verifier}).toString()
      });
      const claims=verifyToken(token.id_token,metadata,keys,c.id,row.nonce,provider,this.msTenant);
      identity={subject:String(claims.verifiedSubject||claims.sub),name:String(claims.name||claims.preferred_username||'Client'),
        email:provider==='google'?(claims.email_verified===true&&typeof claims.email==='string'?claims.email:null):
          claims.email||claims.preferred_username||null};
    }
    if(identity.subject.length>300||identity.name.length>200)throw reject(401,'Invalid provider profile.');
    const token=rand();
    this.store.db.prepare('INSERT INTO client_auth_sessions VALUES(?,?,?,?,?,?)')
      .run(digest(token),provider,identity.subject,identity.name,identity.email,Date.now()+28800000);
    res.writeHead(302,{Location:this.origin+'/#galleries',
      'Set-Cookie':['bryans_auth_state=; Path=/auth/; HttpOnly; SameSite=Lax; Max-Age=0'+(this.secure?'; Secure':''),
        'bryans_client_session='+token+'; Path=/; HttpOnly; SameSite=Lax; Max-Age=28800'+(this.secure?'; Secure':'')],
      'Cache-Control':'no-store'}).end();
  }
  logout(req,res){
    const token=this.token(req);if(token)this.store.db.prepare('DELETE FROM client_auth_sessions WHERE token_hash=?').run(digest(token));
    res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store',
      'Set-Cookie':'bryans_client_session=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0'+(this.secure?'; Secure':'')}).end('{"ok":true}');
  }
  async handle(req,res,url,host,json){
    if(req.method==='GET'&&url.pathname==='/api/auth/providers'){json(res,200,{providers:this.providers()});return true;}
    const start=url.pathname.match(/^\/auth\/(facebook|microsoft|yahoo|google)$/);
    const done=url.pathname.match(/^\/auth\/(facebook|microsoft|yahoo|google)\/callback$/);
    if(!start&&!done)return false;
    if(host!==this.host)throw reject(404,'Sign-in is available on the web hostname only.');
    if(req.method!=='GET')throw reject(405,'Method not allowed.');
    if(start)await this.begin(start[1],res);else await this.finish(done[1],url,res,req);
    return true;
  }
}
