import { randomUUID, randomBytes, timingSafeEqual, createHash } from 'node:crypto';
import { readRosterFile } from './roster-file.mjs';
import { IntakeStore, validateRoster, fail } from './intake-store.mjs';
import { WorkDayExchangeStore } from './workday-exchange-store.mjs';
import { join } from 'node:path';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';
const run = promisify(execFile);
const equal=(a,b)=>{const x=Buffer.from(a||''),y=Buffer.from(b||'');return x.length===y.length&&timingSafeEqual(x,y);};
export function createIntakeApi(env,readBody,json) {
  const store=new IntakeStore(env.INTAKE_DB_PATH || './data/intake.sqlite');
  const workdayStore=new WorkDayExchangeStore(env.WORKDAY_EXCHANGE_DB_PATH||env.INTAKE_DB_PATH||'./data/intake.sqlite');
  function workdayToken(req){
    const header=String(req.headers['x-aegis-workday-token']||'');
    if(header)return header;
    const cookie=String(req.headers.cookie||'').split(';').map(x=>x.trim()).find(x=>x.startsWith('aegis_workday_session='));
    return cookie?cookie.slice('aegis_workday_session='.length):'';
  }
  function requireWorkday(req){const identity=workdayStore.authenticate(workdayToken(req));if(!identity)fail(401,'workday_unauthorized','Sign in to WorkDay again.');return identity;}

  let credentials;try{credentials=JSON.parse(env.PUBLIC_API_KEYS_JSON || '[]');}catch{throw Error('Invalid PUBLIC_API_KEYS_JSON');}
  if(!Array.isArray(credentials)||credentials.some(c=>!c.id||typeof c.id!=='string'||typeof c.secret!=='string'||c.secret.length<32||!Array.isArray(c.scopes))||new Set(credentials.map(c=>c.id)).size!==credentials.length)throw Error('Configure unique integration credential IDs, secrets of at least 32 characters, and scopes.');
  const rates=new Map();
  function authenticate(req,scope){
    const token=(req.headers.authorization||'').replace(/^Bearer /,'');
    const managed=store.authenticateApiCredential(token,scope);if(managed)return managed;
    const c=credentials.find(x=>x.secret?.length>=32&&equal(token,x.secret));
    if(!c)fail(401,'unauthorized','A valid integration credential is required.');
    if(!Array.isArray(c.scopes)||!c.scopes.includes(scope))fail(403,'wrong_scope','Credential lacks the required scope.');
    const minute=Math.floor(Date.now()/60000),key=`${c.id}:${minute}`,n=(rates.get(key)||0)+1;rates.set(key,n);
    if(rates.size>2000)for(const k of rates.keys())if(!k.endsWith(`:${minute}`))rates.delete(k);
    if(n>120)fail(429,'rate_limited','Retry after the current minute.');return c.id;
  }
  function authenticateProvisioning(req){
    const expected=env.PUBLIC_API_PROVISIONING_TOKEN||'',supplied=String(req.headers['x-site-provisioning-token']||'');
    if(expected.length<32||!equal(supplied,expected))fail(401,'provisioning_unauthorized','Valid server-side provisioning credentials are required.');
  }
  async function input(req,limit){try{const p=JSON.parse((await readBody(req,limit)).toString('utf8'));if(!p||typeof p!=='object'||Array.isArray(p))fail(400,'invalid_json','A JSON object is required.');return p;}catch(e){if(e.status)throw e;fail(400,'invalid_json','Supply valid JSON.');}}
  async function sameOrigin(req,url){const origin=req.headers.origin;try{if(!origin||new URL(origin).host!==req.headers.host)throw Error();}catch{fail(403,'origin_rejected','Same-origin browser request required.');}
    const cookies=(req.headers.cookie||'').split(';').map(x=>x.trim());
    if(!req.headers['x-csrf-token']||!equal(cookies.find(x=>x.startsWith('intake_csrf='))?.slice(12),req.headers['x-csrf-token']))fail(403,'csrf_rejected','Reload the form before submitting.');
  }
  async function scan(bytes,name){
    // File bytes are never rendered, decoded or processed before type checks and scanning.
    const jpg=bytes[0]===255&&bytes[1]===216&&bytes[2]===255;
    const png=bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10]));
    const pdf=bytes.subarray(0,5).toString()==='%PDF-';
    const ext=name.split('.').pop()?.toLowerCase();
    if(!(jpg&&['jpg','jpeg'].includes(ext)||png&&ext==='png'||pdf&&ext==='pdf'))return {state:'Rejected',type:'application/octet-stream'};
    const type=jpg?'image/jpeg':png?'image/png':'application/pdf';
    if(!env.MALWARE_SCANNER_PATH)return {state:'Quarantined',type};
    const dir=await mkdtemp(join(tmpdir(),'studio-scan-'));
    try{const path=join(dir,'upload');await writeFile(path,bytes,{mode:0o600});await run(env.MALWARE_SCANNER_PATH,['--no-summary',path],{timeout:30000,maxBuffer:4096});return {state:'Clean',type};}
    catch{return {state:'Quarantined',type};}finally{await rm(dir,{recursive:true,force:true});}
  }
  return {store,workdayStore,async handle(req,res,url){
    const route=url.pathname, method=req.method;
    if(route==='/api/v1/auth/login'&&method==='POST'){
      const p=await input(req,8192);
      const minute=Math.floor(Date.now()/60000),identity=String(p.userName||'').trim().toLowerCase().slice(0,200);
      const key=`workday-login:${req.socket?.remoteAddress||'unknown'}:${identity}:${minute}`;
      const attempts=(rates.get(key)||0)+1;rates.set(key,attempts);
      if(rates.size>2000)for(const candidate of rates.keys())if(!candidate.endsWith(`:${minute}`))rates.delete(candidate);
      if(attempts>10)fail(429,'rate_limited','Too many sign-in attempts. Retry after the current minute.');
      const result=workdayStore.login(p.userName,p.password,p.deviceId||'');
      json(res,200,{userName:result.userName,displayName:result.displayName,expiresAt:result.expiresAt},{'Set-Cookie':`aegis_workday_session=${result.token}; Path=/api/v1/; HttpOnly; SameSite=Lax; Max-Age=28800${env.NODE_ENV==='production'?'; Secure':''}`,'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/auth/logout'&&method==='POST'){
      workdayStore.revoke(workdayToken(req));json(res,200,{signedOut:true},{'Set-Cookie':'aegis_workday_session=; Path=/api/v1/; HttpOnly; SameSite=Lax; Max-Age=0'});return true;
    }
    if(route==='/api/v1/auth/me'&&method==='GET'){
      const user=requireWorkday(req);json(res,200,{userName:user.user_name,displayName:user.display_name});return true;
    }
    if(route==='/api/v1/workday/enroll'&&method==='POST'){
      const p=await input(req,8192),user=workdayStore.enroll(p.enrollmentToken,p.password);
      json(res,200,user,{'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/admin/workday/users'&&method==='POST'){
      authenticate(req,'workday:sync');const result=workdayStore.provisionAccount(await input(req,16384));
      json(res,201,result,{'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/admin/workday/snapshots'&&method==='PUT'){
      authenticate(req,'workday:sync');const p=await input(req,5_000_000);
      json(res,200,workdayStore.publishSnapshot(p.userName,p.revision,p.snapshot));return true;
    }
    if(route==='/api/v1/admin/workday/updates'&&method==='GET'){
      authenticate(req,'workday:sync');const after=Math.max(0,Number(url.searchParams.get('after')||0)),limit=Math.max(1,Math.min(500,Number(url.searchParams.get('limit')||100)));
      json(res,200,{items:workdayStore.pendingUpdates(after,limit,String(url.searchParams.get('kind')||''))});return true;
    }
    const workdayResult=route.match(/^\/api\/v1\/admin\/workday\/updates\/([0-9a-f-]{36})\/result$/i);
    if(workdayResult&&method==='POST'){
      authenticate(req,'workday:sync');json(res,200,workdayStore.completeUpdate(workdayResult[1],await input(req,65536)));return true;
    }
    if(route==='/api/v1/workday/device-session'&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,8192);
      if(typeof p.deviceId!=='string'||!p.deviceId.trim())fail(400,'invalid_device','Device ID is required.');
      const result=workdayStore.issueDeviceSession(user.user_name,p.deviceId);
      json(res,200,{sessionId:randomUUID(),token:result.token,expiresAtUtc:result.expiresAtUtc},{'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/workday/device-session/revoke'&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,8192);
      workdayStore.revokeDevice(user.user_name,p.deviceId);res.writeHead(204).end();return true;
    }
    if(route==='/api/v1/workday/bootstrap'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      if(!snapshot)fail(503,'workday_snapshot_pending','Studio has not published this WorkDay account yet.');
      json(res,200,snapshot,{'Cache-Control':'private, no-store'});return true;
    }
    if(route==='/api/v1/workday/operations'&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,65536),result=workdayStore.enqueue(user.user_name,p.operationId,'Operation',p);
      json(res,202,result,{'Cache-Control':'no-store'});return true;
    }
    const workdayStatus=route.match(/^\/api\/v1\/workday\/operations\/([0-9a-f-]{36})$/i);
    if(workdayStatus&&method==='GET'){
      const user=requireWorkday(req),result=workdayStore.updateStatus(user.user_name,workdayStatus[1]);
      if(!result)fail(404,'not_found','WorkDay operation not found.');
      json(res,200,result,{'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/workday/client-observation'&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,16384);
      if(typeof p.deviceId!=='string'||!p.deviceId||p.deviceId.length>200)fail(400,'invalid_observation','Device ID is required.');
      workdayStore.observe(user.user_name,p.deviceId,p);json(res,202,{accepted:true});return true;
    }
    if(route==='/api/v1/workday/availability'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      if(!snapshot)fail(503,'workday_snapshot_pending','Studio has not published availability yet.');
      json(res,200,snapshot.availability||[],{'Cache-Control':'private, no-store'});return true;
    }
    if(route==='/api/v1/workday/time-summary'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      if(!snapshot)fail(503,'workday_snapshot_pending','Studio has not published time summaries yet.');
      json(res,200,snapshot.timeSummary||null,{'Cache-Control':'private, no-store'});return true;
    }
    if(route==='/api/v1/notifications'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.notifications||[],{'Cache-Control':'private, no-store'});return true;
    }
    if(route==='/api/v1/messages/threads'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.threads||[],{'Cache-Control':'private, no-store'});return true;
    }

    if(route==='/api/v1/workday/availability'&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,16384),operationId=p.operationId||randomUUID();
      const result=workdayStore.enqueue(user.user_name,operationId,'AvailabilityUpsert',{...p,operationId});
      json(res,202,{id:operationId,userName:user.user_name,startsAtUtc:p.startsAtUtc,endsAtUtc:p.endsAtUtc,availabilityType:p.availabilityType,note:p.note,createdAtUtc:new Date().toISOString(),syncState:result.state});return true;
    }
    if(route==='/api/v1/workday/time-summary'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.timeSummary||null,{'Cache-Control':'private, no-store'});return true;
    }
    const assignmentUpload=route.match(/^\/api\/v1\/workday\/assignments\/([0-9a-f-]{36})\/uploads$/i);
    if(assignmentUpload&&method==='POST'){
      const user=requireWorkday(req),bytes=await readBody(req,30_000_000);
      const request=new Request('http://localhost/upload',{method:'POST',headers:{'Content-Type':req.headers['content-type']||''},body:bytes});
      const form=await request.formData(),file=form.get('file'),uploadId=String(form.get('uploadId')||'');
      if(!file||typeof file==='string'||!/^[0-9a-f-]{36}$/i.test(uploadId))fail(400,'invalid_upload','Upload ID and file are required.');
      const content=Buffer.from(await file.arrayBuffer());
      const result=workdayStore.enqueue(user.user_name,uploadId,'AssignmentUpload',{assignmentId:assignmentUpload[1],uploadId,fileName:file.name,contentType:file.type||'application/octet-stream',contentBase64:content.toString('base64')});
      json(res,202,{uploadId,attachmentId:uploadId,assignmentId:assignmentUpload[1],fileName:file.name,sizeBytes:content.length,storageStatus:result.state,acknowledgedAtUtc:new Date().toISOString()},{'Cache-Control':'no-store'});return true;
    }
    const galleryStatus=route.match(/^\/api\/v1\/workday\/assignments\/([0-9a-f-]{36})\/gallery-status$/i);
    if(galleryStatus&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.galleryStatus?.[galleryStatus[1]]||{exists:false,isPublished:false,visibleItems:0},{'Cache-Control':'private, no-store'});return true;
    }
    if(route==='/api/v1/notifications'&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.notifications||[],{'Cache-Control':'private, no-store'});return true;
    }
    const threadCollection=route.match(/^\/api\/v1\/messages\/threads(?:\/([0-9a-f-]{36}))?$/i);
    if(threadCollection&&threadCollection[1]&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name),threadId=threadCollection[1];
      json(res,200,(snapshot?.messages||[]).filter(x=>String(x.threadId).toLowerCase()===threadId.toLowerCase()),{'Cache-Control':'private, no-store'});return true;
    }
    if(threadCollection&&threadCollection[1]&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,16384),operationId=String(req.headers['idempotency-key']||randomUUID());
      json(res,202,workdayStore.enqueue(user.user_name,operationId,'ThreadMessage',{threadId:threadCollection[1],body:p.body}));return true;
    }
    const createThread=route.match(/^\/api\/v1\/messages\/threads\/([A-Za-z0-9._~-]{1,40})\/([0-9a-f-]{36})$/i);
    if(createThread&&method==='POST'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      const existing=(snapshot?.threads||[]).find(x=>String(x.scopeType).toLowerCase()===createThread[1].toLowerCase()&&String(x.scopeId).toLowerCase()===createThread[2].toLowerCase());
      if(existing){json(res,200,existing);return true;}
      const operationId=String(req.headers['idempotency-key']||randomUUID());
      json(res,202,workdayStore.enqueue(user.user_name,operationId,'ThreadCreate',{scopeType:createThread[1],scopeId:createThread[2]}));return true;
    }
    const conversationIdentities=route.match(/^\/api\/v1\/conversations\/([0-9a-f-]{36})\/sending-identities$/i);
    if(conversationIdentities&&method==='GET'){
      const user=requireWorkday(req),snapshot=workdayStore.snapshot(user.user_name);
      json(res,200,snapshot?.sendingIdentities?.[conversationIdentities[1]]||{defaultIdentity:'',canSendAsSelf:false,identities:[]},{'Cache-Control':'private, no-store'});return true;
    }
    const conversationMessage=route.match(/^\/api\/v1\/conversations\/([0-9a-f-]{36})\/messages$/i);
    if(conversationMessage&&method==='POST'){
      const user=requireWorkday(req),p=await input(req,16384),operationId=String(req.headers['idempotency-key']||randomUUID());
      json(res,202,workdayStore.enqueue(user.user_name,operationId,'ConversationMessage',{conversationId:conversationMessage[1],...p}));return true;
    }

    const keyCollection='/api/v1/admin/integration-keys',keyItem=route.match(/^\/api\/v1\/admin\/integration-keys\/([0-9a-f-]{36})$/i);
    if(route===keyCollection&&['GET','POST'].includes(method)){
      authenticateProvisioning(req);
      if(method==='GET'){json(res,200,{items:store.listApiCredentials()});return true;}
      const p=await input(req,16384),created=store.createApiCredential(p.name,p.scopes,p.clientApplication);json(res,201,created);return true;
    }
    if(keyItem&&method==='DELETE'){
      authenticateProvisioning(req);if(!store.revokeApiCredential(keyItem[1]))fail(404,'not_found','Integration key not found.');
      json(res,200,{id:keyItem[1],status:'Revoked'});return true;
    }
    if(route==='/api/v1/admin/client-link-proofs'&&method==='POST'){
      authenticate(req,'client:link');const p=await input(req,4096),proof=store.createClientLinkChallenge(p.clientId);
      json(res,201,{id:proof.id,code:proof.code,expiresAt:proof.expiresAt},{'Cache-Control':'no-store'});return true;
    }
    if(route==='/api/v1/admin/client-link-proofs'&&method==='GET'){
      authenticate(req,'client:link');const clientId=url.searchParams.get('clientId');
      if(clientId&&!/^[0-9a-f-]{36}$/i.test(clientId))fail(400,'invalid_client_id','A Studio client UUID is required.');
      json(res,200,{items:store.clientLinkProofs(clientId)});return true;
    }
    if(route==='/api/v1/admin/booking/catalog'&&method==='PUT'){
      const owner=authenticate(req,'booking:catalog:publish'),p=await input(req,1_000_000),key=String(req.headers['idempotency-key']||'');
      if(!/^[A-Za-z0-9._-]{1,90}$/.test(key))fail(400,'idempotency_required','Provide an Idempotency-Key for the catalog publication.');
      json(res,200,store.replay(owner,`catalog-${key}`,p,()=>store.publishBookingCatalog(p)));return true;
    }
    if(route==='/api/v1/admin/booking/availability'&&method==='PUT'){
      const owner=authenticate(req,'booking:availability:publish'),p=await input(req,3_000_000),key=String(req.headers['idempotency-key']||'');
      if(!/^[A-Za-z0-9._-]{1,90}$/.test(key))fail(400,'idempotency_required','Provide an Idempotency-Key for the availability publication.');
      json(res,200,store.replay(owner,`availability-${key}`,p,()=>store.publishBookingAvailability(p,true)));return true;
    }
    if(route==='/api/v1/booking/catalog'&&method==='GET'){
      const catalog=store.getBookingCatalog();json(res,200,catalog,{'Cache-Control':'public, max-age=30'});return true;
    }
    if(route==='/api/v1/booking/availability'&&method==='GET'){
      json(res,200,store.getBookingAvailability({from:url.searchParams.get('from'),to:url.searchParams.get('to'),locationCode:url.searchParams.get('locationCode'),sessionTypeId:url.searchParams.get('sessionTypeId')||''}),{'Cache-Control':'public, max-age=15'});return true;
    }
    const linkProof=route.match(/^\/api\/v1\/admin\/client-link-proofs\/([0-9a-f-]{36})$/i);
    if(linkProof&&method==='GET'){
      authenticate(req,'client:link');const proof=store.clientLinkProof(linkProof[1]);
      if(!proof)fail(404,'not_found','Client link proof not found.');json(res,200,proof);return true;
    }
    if(linkProof&&method==='DELETE'){
      authenticate(req,'client:link');const proof=store.clientLinkProof(linkProof[1]);
      if(!proof)fail(404,'not_found','Client link proof not found.');
      if(proof.status==='Used')fail(409,'proof_already_used','A used proof cannot be revoked; revoke the linked identity through identity administration.');
      if(!store.revokeClientLinkProof(linkProof[1]))fail(409,'proof_not_revocable','Only active pending proofs can be revoked.');
      json(res,200,{id:linkProof[1],status:'Revoked'});return true;
    }
    if(route==='/api/intake-session'&&method==='GET'){
      const csrf=randomUUID();json(res,200,{csrf},{'Set-Cookie':`intake_csrf=${csrf}; Path=/; HttpOnly; SameSite=Strict${env.NODE_ENV==='production'?'; Secure':''}`});return true;
    }
    if(route==='/api/v1/openapi.json'&&method==='GET'){
      const {readFile}=await import('node:fs/promises');json(res,200,JSON.parse(await readFile(new URL('./public-site-v1.openapi.json',import.meta.url),'utf8')));return true;
    }
    const draft=route.match(/^\/api\/roster-drafts(?:\/([0-9a-f-]{36}))?$/i);
    if(draft){
      if(method==='POST'&&!draft[1]){await sameOrigin(req,url);const p=await input(req,2_000_000),rows=validateRoster(p.rows),id=randomUUID(),token=randomBytes(32).toString('base64url');store.db.prepare('INSERT INTO roster_drafts VALUES(?,?,1,?)').run(id,createHash('sha256').update(token).digest('hex'),JSON.stringify(rows));json(res,201,{id,accessToken:token,revision:1,rows});return true;}
      const r=store.db.prepare('SELECT * FROM roster_drafts WHERE id=?').get(draft[1]);if(!r||!equal(r.access_hash,createHash('sha256').update(String(req.headers['x-intake-token']||'')).digest('hex')))fail(404,'not_found','Roster draft not found.');
      if(method==='GET'){json(res,200,{revision:r.revision,rows:JSON.parse(r.rows)});return true;}
      if(method==='PUT'){await sameOrigin(req,url);const p=await input(req,2_000_000),rows=validateRoster(p.rows);const n=store.db.prepare('UPDATE roster_drafts SET rows=?,revision=revision+1 WHERE id=? AND revision=?').run(JSON.stringify(rows),draft[1],p.revision);if(!n.changes)fail(409,'revision_conflict','Reload the draft before saving.');json(res,200,{revision:r.revision+1,rows});return true;}
      fail(405,'method_not_allowed','Unsupported draft operation.');
    }
    if(route==='/api/roster-file'&&method==='POST'){
      await sameOrigin(req,url);const body=await readBody(req,5_100_000);
      const request=new Request('http://localhost/upload',{method:'POST',headers:{'Content-Type':req.headers['content-type']||''},body});
      const form=await request.formData(),file=form.get('file');if(!file||typeof file==='string')fail(400,'roster_required','Choose a roster file.');
      const csv=await readRosterFile(Buffer.from(await file.arrayBuffer()),file.name);
      // Return the text to the same reusable editor for column mapping and explicit commit.
      json(res,200,{csv});return true;
    }
    if(!route.startsWith('/api/v1/')&&!/^\/api\/(bookings|inquiries)(\/|$)/.test(route))return false;
    const browser=/^\/api\/(bookings|inquiries)/.test(route);
    if(browser&&['POST','PUT','DELETE'].includes(method))await sameOrigin(req,url);
    const submit=route==='/api/bookings'||route==='/api/inquiries'||route==='/api/v1/intakes';
    if(submit&&method==='POST'){
      const owner=browser?'reference-site-form':authenticate(req,'intake:submit');
      const value=await input(req,2_000_000);if(route==='/api/inquiries')value.kind='Inquiry';
      const clientApplication=browser?'API':store.apiClientApplication(owner)||credentials.find(x=>x.id===owner)?.clientApplication||'API';
      const result=store.create(owner,String(req.headers['idempotency-key']||''),value,browser?'Form':'Api',clientApplication);json(res,201,result);return true;
    }
    if(route==='/api/v1/changes'&&method==='GET'){authenticate(req,'intake:pull');json(res,200,store.feed(url.searchParams.get('cursor')||''));return true;}
    if(route==='/api/v1/gallery-access-requests'&&method==='POST'){
      const owner=authenticate(req,'gallery:access:submit');const p=await input(req,8192);
      const key=String(req.headers['idempotency-key']||'').trim();
      if(!key||key.length>200)fail(400,'invalid_idempotency_key','A unique Idempotency-Key of at most 200 characters is required.');
      const requestKey=createHash('sha256').update(owner+':'+key).digest('hex');
      const result=store.createGalleryAccessRequest({...p,requestKey});
      json(res,result.created?201:200,{id:result.id,status:result.status,accepted:true});return true;
    }
    if(route==='/api/v1/gallery-access-requests'&&method==='GET'){
      authenticate(req,'gallery:access:pull');json(res,200,{items:store.pendingGalleryAccessRequests(Math.max(1,Math.min(500,Number(url.searchParams.get('limit')||100))))});return true;
    }
    const galleryAccessAck=route.match(/^\/api\/v1\/gallery-access-requests\/([0-9a-f-]{36})\/ack$/i);
    if(galleryAccessAck&&method==='POST'){
      authenticate(req,'gallery:access:ack');json(res,200,store.acknowledgeGalleryAccessRequest(galleryAccessAck[1]));return true;
    }
    const match=route.match(/^\/api\/v1\/intakes\/([0-9a-f-]{36})(?:\/(ack|status|roster|attachments|payments))?$/i)||route.match(/^\/api\/(?:bookings|inquiries)\/([0-9a-f-]{36}|\d{4}-(?:BK|BE|BR|W)-\d{6})(?:\/(roster|attachments))?$/i);
    if(match){
      const id=match[1],action=match[2],token=String(req.headers['x-intake-token']||'');
      if(action==='ack'&&method==='POST'){authenticate(req,'intake:ack');const p=await input(req);const statuses={};for(const k of ['reviewStatus','bookingStatus','acknowledgmentStatus','paymentStatus'])if(p[k]!==undefined){if(typeof p[k]!=='string'||p[k].length>100)fail(400,'invalid_status','Invalid status.');statuses[k]=p[k];}json(res,200,store.ack(id,p.revision,statuses));return true;}
      if(action==='payments'&&method==='POST'){authenticate(req,'payment:verified');json(res,200,store.payment(id,await input(req)));return true;}
      const owner=browser?'':authenticate(req,'intake:submit');
      if(method==='GET'&&(!action||action==='status')){json(res,200,store.status(id,owner,token));return true;}
      if(action==='roster'&&method==='PUT'){const p=await input(req,2_000_000);json(res,200,store.roster(id,owner,token,p.revision,p.rows));return true;}
      if(action==='attachments'&&method==='POST'){
        store.get(id,owner,token);const p=await input(req,4_000_000);
        if(typeof p.fileName!=='string'||p.fileName.length>200||typeof p.contentBase64!=='string')fail(400,'invalid_attachment','Filename and base64 content required.');
        const bytes=Buffer.from(p.contentBase64,'base64');if(!bytes.length||bytes.length>2_500_000)fail(413,'attachment_limit','Maximum file size is 2.5 MB.');
        const result=await scan(bytes,p.fileName),file={id:randomUUID(),fileName:p.fileName,contentType:result.type,size:bytes.length,state:result.state,sha256:createHash('sha256').update(bytes).digest('hex')};
        store.transaction(()=>{const r=store.get(id,owner,token),v=JSON.parse(r.payload);if(v.attachments.length>=10||v.attachments.reduce((n,a)=>n+a.size,0)+bytes.length>10_000_000)fail(413,'attachment_limit','Maximum ten attachments totaling 10 MB.');
          store.db.prepare('INSERT INTO attachments VALUES(?,?,?,?,?)').run(file.id,id,JSON.stringify(file),bytes,file.state);v.attachments.push(file);v.revision++;store.db.prepare('UPDATE intakes SET payload=?,revision=? WHERE id=?').run(JSON.stringify(v),v.revision,id);store.change(v);});
        json(res,201,file);return true;
      }
    }
    const rescan=route.match(/^\/api\/v1\/attachments\/([0-9a-f-]{36})\/rescan$/i);
    if(rescan&&method==='POST'){
      authenticate(req,'attachment:scan');const r=store.db.prepare('SELECT * FROM attachments WHERE id=?').get(rescan[1]);if(!r)fail(404,'not_found','Attachment not found.');const m=JSON.parse(r.metadata),result=await scan(Buffer.from(r.content),m.fileName);m.state=result.state;
      store.transaction(()=>{store.db.prepare('UPDATE attachments SET state=?,metadata=? WHERE id=?').run(m.state,JSON.stringify(m),m.id);const row=store.db.prepare('SELECT payload FROM intakes WHERE id=?').get(r.intake_id),p=JSON.parse(row.payload);p.attachments=p.attachments.map(x=>x.id===m.id?m:x);p.revision++;store.db.prepare('UPDATE intakes SET payload=?,revision=? WHERE id=?').run(JSON.stringify(p),p.revision,p.id);store.change(p);});json(res,200,m);return true;
    }
    const file=route.match(/^\/api\/v1\/attachments\/([0-9a-f-]{36})$/i);
    if(file&&method==='GET'){authenticate(req,'intake:pull');const r=store.db.prepare("SELECT metadata,content FROM attachments WHERE id=? AND state='Clean'").get(file[1]);if(!r)fail(404,'unavailable','Attachment is unavailable or quarantined.');const m=JSON.parse(r.metadata);res.writeHead(200,{'Content-Type':m.contentType,'Cache-Control':'no-store','Content-Disposition':'attachment'});res.end(r.content);return true;}
    if(route==='/api/v1/galleries'&&method==='POST'){
      authenticate(req,'gallery:publish');const p=await input(req,1_000_000);if(!/^[0-9a-f-]{36}$/i.test(p.id)||!p.clientId||!Array.isArray(p.items)||p.items.length>10000||!p.title)fail(400,'invalid_gallery','Gallery ID, client ID, title and items required.');
      store.transaction(()=>{const old=store.db.prepare('SELECT client_id FROM galleries WHERE id=?').get(p.id);if(old&&old.client_id!==p.clientId)fail(409,'gallery_owner','Cannot change gallery owner.');store.db.prepare('INSERT INTO galleries VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload').run(p.id,p.clientId,JSON.stringify({...p,published:false}));});json(res,200,{reference:p.id,provider:'PublicSite',storedVersionIds:store.db.prepare('SELECT id FROM media WHERE gallery_id=?').all(p.id).map(x=>x.id)});return true;
    }
    const publication=route.match(/^\/api\/v1\/galleries\/([0-9a-f-]{36})\/(publish|revoke)$/i);
    if(publication&&method==='POST'){
      authenticate(req,'gallery:publish');store.transaction(()=>{const row=store.db.prepare('SELECT payload FROM galleries WHERE id=?').get(publication[1]);if(!row)fail(404,'not_found','Gallery not found.');const p=JSON.parse(row.payload);
        if(publication[2]==='publish'&&p.items.some(x=>!store.db.prepare('SELECT 1 FROM media WHERE gallery_id=? AND id=?').get(p.id,x.versionId)))fail(409,'media_pending','All media must be stored before publishing.');
        p.published=publication[2]==='publish';store.db.prepare('UPDATE galleries SET payload=? WHERE id=?').run(JSON.stringify(p),p.id);});json(res,200,{state:publication[2]==='publish'?'Published':'Revoked'});return true;
    }
    const media=route.match(/^\/api\/v1\/galleries\/([0-9a-f-]{36})\/media\/([0-9a-f-]{36})$/i);
    if(media&&method==='PUT'){
      authenticate(req,'gallery:publish');const r=store.db.prepare('SELECT payload FROM galleries WHERE id=?').get(media[1]);if(!r||!JSON.parse(r.payload).items.some(x=>x.versionId===media[2]))fail(404,'not_found','Gallery item not registered.');
      const bytes=await readBody(req,30_000_000),type=String(req.headers['content-type']||'application/octet-stream');
      if(!['image/jpeg','image/png','image/webp'].includes(type))fail(400,'invalid_type','Only raster gallery images accepted.');
      const old=store.db.prepare('SELECT type,content FROM media WHERE gallery_id=? AND id=?').get(media[1],media[2]);if(old&&(old.type!==type||!Buffer.from(old.content).equals(bytes)))fail(409,'media_conflict','A version ID cannot be reused for different bytes.');
      store.db.prepare('INSERT INTO media VALUES(?,?,?,?) ON CONFLICT(gallery_id,id) DO UPDATE SET type=excluded.type,content=excluded.content').run(media[1],media[2],type,bytes);json(res,200,{stored:true});return true;
    }
    fail(404,'not_found','Operation not found.');
  }};
}


