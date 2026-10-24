import { DatabaseSync } from 'node:sqlite';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';

export class IntakeError extends Error {
  constructor(status, code, message, fields = {}) { super(message); Object.assign(this, { status, code, fields }); }
}
export const fail = (status, code, message, fields) => { throw new IntakeError(status, code, message, fields); };
const fingerprint = x => createHash('sha256').update(JSON.stringify(x)).digest('hex');
export function validateRoster(rows = []) {
  if (!Array.isArray(rows) || rows.length > 5000) fail(400, 'invalid_roster', 'Supply at most 5000 subjects.');
  const seen = new Set();
  const limits={name:200,reference:200,collection:100,contactName:200,contactEmail:320,contactPhone:100,scheduledAtUtc:50,scheduleLocation:300,notes:4000,pickupNotification:8};
  return rows.map((row, i) => {
    if (!row || typeof row !== 'object') fail(400, 'invalid_roster', 'Invalid row.', { [i]: 'Object required.' });
    const result = {};
    for (const key of ['name','reference','collection','contactName','contactEmail','contactPhone','scheduledAtUtc','scheduleLocation','notes','pickupNotification']) {
      if (row[key] === undefined) continue;
      if (typeof row[key] !== 'string' || row[key].length > limits[key]) fail(400, 'invalid_roster', 'Invalid roster value.', { [`${i}.${key}`]: 'Text exceeds limit or has invalid type.' });
      result[key] = row[key].trim();
    }
    if (!result.name || result.name.toLowerCase() === 'unclassified') fail(400, 'invalid_roster', 'Each subject needs a name; Unclassified is reserved.', { [`${i}.name`]: 'Name required.' });
    if (result.contactEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(result.contactEmail)) fail(400, 'invalid_roster', 'Correct contact email.', { [`${i}.contactEmail`]: 'Invalid email.' });
    const preference = result.pickupNotification || 'None';
    if (!['None', 'Email', 'WhatsApp'].includes(preference) || preference === 'Email' && !result.contactEmail || preference === 'WhatsApp' && !/^\+\d{8,15}$/.test(result.contactPhone || '')) fail(400, 'invalid_roster', 'Notification preference needs a valid contact channel.', { [`${i}.pickupNotification`]: 'Invalid channel or missing contact.' });
    if (result.scheduledAtUtc && !Number.isFinite(Date.parse(result.scheduledAtUtc))) fail(400, 'invalid_roster', 'Invalid subject schedule.');
    result.reference ||= randomUUID(); result.pickupNotification = preference;
    if (seen.has(result.reference.toLowerCase())) fail(400, 'duplicate_subject', 'Resolve duplicate subject references before saving.');
    seen.add(result.reference.toLowerCase()); return result;
  });
}
export class IntakeStore {
  constructor(path, now = () => new Date()) {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    this.db = new DatabaseSync(path); this.now = now;
    this.db.exec(`PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;
      CREATE TABLE IF NOT EXISTS roster_drafts(id TEXT PRIMARY KEY,access_hash TEXT NOT NULL,revision INTEGER NOT NULL,rows TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS counters(year INTEGER,source TEXT,value INTEGER,PRIMARY KEY(year,source));
      CREATE TABLE IF NOT EXISTS intakes(id TEXT PRIMARY KEY,reference TEXT UNIQUE,owner TEXT NOT NULL,payload TEXT NOT NULL,revision INTEGER NOT NULL,ack_revision INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT '{}',created_at TEXT NOT NULL,access_hash TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS changes(sequence INTEGER PRIMARY KEY AUTOINCREMENT,intake_id TEXT NOT NULL,revision INTEGER NOT NULL,payload TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS replays(owner TEXT,key TEXT,hash TEXT,result TEXT,PRIMARY KEY(owner,key));
      CREATE TABLE IF NOT EXISTS attachments(id TEXT PRIMARY KEY,intake_id TEXT,metadata TEXT,content BLOB,state TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS galleries(id TEXT PRIMARY KEY,client_id TEXT NOT NULL,payload TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS api_credentials(id TEXT PRIMARY KEY,name TEXT NOT NULL,secret_hash TEXT NOT NULL UNIQUE,scopes TEXT NOT NULL,created_at TEXT NOT NULL,last_used_at TEXT,revoked_at TEXT,client_application TEXT NOT NULL DEFAULT 'API');
      CREATE TABLE IF NOT EXISTS client_link_challenges(id TEXT PRIMARY KEY,code_hash TEXT NOT NULL UNIQUE,client_id TEXT NOT NULL,expires_at TEXT NOT NULL,used_at TEXT,revoked_at TEXT,created_at TEXT NOT NULL,provider TEXT);
      CREATE TABLE IF NOT EXISTS client_identity_links(provider TEXT NOT NULL,subject TEXT NOT NULL,client_id TEXT NOT NULL,verified_at TEXT NOT NULL,revoked_at TEXT,PRIMARY KEY(provider,subject));
      CREATE TABLE IF NOT EXISTS media(gallery_id TEXT,id TEXT,type TEXT,content BLOB,PRIMARY KEY(gallery_id,id));
      CREATE TABLE IF NOT EXISTS selections(gallery_id TEXT,id TEXT,selected INTEGER,PRIMARY KEY(gallery_id,id));
      CREATE TABLE IF NOT EXISTS payment_events(id TEXT PRIMARY KEY,intake_id TEXT NOT NULL,hash TEXT NOT NULL,payload TEXT NOT NULL);`);
    this.db.exec(`CREATE TABLE IF NOT EXISTS booking_catalog(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,payload TEXT NOT NULL,published_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS booking_slots(slot_id TEXT PRIMARY KEY,location_code TEXT NOT NULL,session_type_id TEXT NOT NULL,starts_at TEXT NOT NULL,ends_at TEXT NOT NULL,catalog_revision INTEGER NOT NULL,published_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS booking_slot_reservations(slot_id TEXT PRIMARY KEY REFERENCES booking_slots(slot_id),intake_id TEXT NOT NULL UNIQUE REFERENCES intakes(id),reserved_at TEXT NOT NULL);`);
    if(!this.db.prepare('PRAGMA table_info(api_credentials)').all().some(x=>x.name==='client_application'))this.db.exec("ALTER TABLE api_credentials ADD COLUMN client_application TEXT NOT NULL DEFAULT 'API'");
    if(!this.db.prepare('PRAGMA table_info(client_link_challenges)').all().some(x=>x.name==='provider'))this.db.exec('ALTER TABLE client_link_challenges ADD COLUMN provider TEXT');
  }
  transaction(action) { this.db.exec('BEGIN IMMEDIATE'); try { const result = action(); this.db.exec('COMMIT'); return result; } catch (e) { this.db.exec('ROLLBACK'); throw e; } }
  createApiCredential(name, scopes, clientApplication='API') {
    if(typeof name!=='string'||!name.trim()||name.trim().length>100) fail(400,'invalid_key_name','Provide a key name up to 100 characters.');
    const allowed=new Set(['intake:submit','intake:pull','intake:ack','bookings:read','availability:read','booking:catalog:publish','booking:availability:publish','gallery:publish','gallery:read','client:link','attachment:scan','payment:verified','workday:sync']);
    if(!Array.isArray(scopes)||!scopes.length||scopes.some(x=>!allowed.has(x))||new Set(scopes).size!==scopes.length) fail(400,'invalid_scopes','Choose one or more supported, unique scopes.');
    if(!['API','Workday Mobile','Studio'].includes(clientApplication))fail(400,'invalid_client_application','Choose API, Workday Mobile or Studio as the credential application.');
    const id=randomUUID(),secret=randomBytes(32).toString('base64url'),createdAt=this.now().toISOString();
    this.db.prepare('INSERT INTO api_credentials(id,name,secret_hash,scopes,created_at,client_application) VALUES(?,?,?,?,?,?)').run(id,name.trim(),createHash('sha256').update(secret).digest('hex'),JSON.stringify(scopes),createdAt,clientApplication);
    return {id,name:name.trim(),scopes,clientApplication,createdAt,secret};
  }
  listApiCredentials() {
    return this.db.prepare('SELECT id,name,scopes,client_application,created_at,last_used_at,revoked_at FROM api_credentials ORDER BY created_at DESC').all().map(x=>({...x,clientApplication:x.client_application,scopes:JSON.parse(x.scopes),status:x.revoked_at?'Revoked':'Active'}));
  }
  revokeApiCredential(id) {
    const result=this.db.prepare('UPDATE api_credentials SET revoked_at=COALESCE(revoked_at,?) WHERE id=?').run(this.now().toISOString(),id);
    return result.changes>0;
  }
  getBookingCatalog() {
    const row=this.db.prepare('SELECT revision,payload,published_at FROM booking_catalog WHERE id=1').get();
    return row?{...JSON.parse(row.payload),revision:row.revision,publishedAt:row.published_at}:{revision:0,sessionTypes:[],publishedAt:null};
  }
  publishBookingCatalog(input) {
    if(!input||!Array.isArray(input.sessionTypes)||input.sessionTypes.length>200)fail(400,'invalid_catalog','Publish between 1 and 200 Studio session types.');
    const ids=new Set(),names=new Set();
    const sessionTypes=input.sessionTypes.map((x,i)=>{
      if(!x||typeof x.id!=='string'||!/^[A-Za-z0-9._-]{1,100}$/.test(x.id)||ids.has(x.id))fail(400,'invalid_catalog','Session type IDs must be unique, stable identifiers.',{[`sessionTypes.${i}.id`]:'Invalid or duplicate ID.'});
      ids.add(x.id);
      if(typeof x.name!=='string'||!x.name.trim()||x.name.trim().length>160||names.has(x.name.trim().toLowerCase()))fail(400,'invalid_catalog','Session names must be unique and non-empty.',{[`sessionTypes.${i}.name`]:'Invalid or duplicate name.'});
      names.add(x.name.trim().toLowerCase());
      for(const field of ['durationMinutes','setupBufferMinutes','cleanupBufferMinutes','priceMinor','depositMinor'])if(!Number.isSafeInteger(x[field])||x[field]<0)fail(400,'invalid_catalog',`Invalid ${field}.`,{[`sessionTypes.${i}.${field}`]:'A nonnegative integer is required.'});
      if(x.durationMinutes<15||x.durationMinutes>1440||x.setupBufferMinutes>480||x.cleanupBufferMinutes>480||x.depositMinor>x.priceMinor||x.currency!=='JMD')fail(400,'invalid_catalog','Session duration, deposit or currency is invalid.',{[`sessionTypes.${i}`]:'Use JMD and a deposit no greater than the price.'});
      if(!Array.isArray(x.locations)||!x.locations.length||x.locations.some(v=>!['BK','BE'].includes(v))||new Set(x.locations).size!==x.locations.length)fail(400,'invalid_catalog','Choose one or more supported Studio locations.',{[`sessionTypes.${i}.locations`]:'Use BK and/or BE.'});
      const requirements=x.resourceRequirements||{};
      if(!requirements||typeof requirements!=='object'||Array.isArray(requirements)||Object.entries(requirements).some(([k,v])=>!/^[A-Za-z0-9._-]{1,80}$/.test(k)||!Number.isInteger(v)||v<0||v>20))fail(400,'invalid_catalog','Resource requirements must map resource types to integer counts.');
      return {id:x.id,name:x.name.trim(),description:String(x.description||'').trim().slice(0,1000),durationMinutes:x.durationMinutes,setupBufferMinutes:x.setupBufferMinutes,cleanupBufferMinutes:x.cleanupBufferMinutes,priceMinor:x.priceMinor,depositMinor:x.depositMinor,currency:'JMD',locations:[...x.locations],resourceRequirements:requirements,active:x.active!==false};
    });
    const current=this.getBookingCatalog(),revision=current.revision+1,payload=JSON.stringify({sessionTypes}),publishedAt=this.now().toISOString();
    this.db.prepare('INSERT INTO booking_catalog(id,revision,payload,published_at) VALUES(1,?,?,?) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,published_at=excluded.published_at').run(revision,payload,publishedAt);
    return {revision,sessionTypes,publishedAt};
  }
  publishBookingAvailability(input, alreadyInTransaction=false) {
    if(!input||!Array.isArray(input.slots)||input.slots.length>20000)fail(400,'invalid_availability','Publish at most 20000 Studio availability slots.');
    const catalog=this.getBookingCatalog(),ids=new Set(),slots=input.slots.map((x,i)=>{
      if(!x||typeof x.id!=='string'||!/^[A-Za-z0-9._-]{1,100}$/.test(x.id)||ids.has(x.id))fail(400,'invalid_availability','Availability slot IDs must be unique and stable.');
      ids.add(x.id);const session=catalog.sessionTypes.find(s=>s.id===x.sessionTypeId&&s.active);
      if(!session||x.catalogRevision!==catalog.revision||!session.locations.includes(x.locationCode)||!['BK','BE'].includes(x.locationCode)||!Number.isFinite(Date.parse(x.startsAt))||!Number.isFinite(Date.parse(x.endsAt))||Date.parse(x.endsAt)-Date.parse(x.startsAt)!==session.durationMinutes*60000)fail(400,'invalid_availability',`Slot ${i+1} does not match the current Studio session catalog.`);
      if(Date.parse(x.startsAt)<=this.now().getTime())fail(400,'invalid_availability','Availability slots must start in the future.');
      return {id:x.id,locationCode:x.locationCode,sessionTypeId:session.id,startsAt:new Date(x.startsAt).toISOString(),endsAt:new Date(x.endsAt).toISOString(),catalogRevision:catalog.revision};
    });
    const save=()=>{
      this.db.prepare('DELETE FROM booking_slots WHERE slot_id NOT IN (SELECT slot_id FROM booking_slot_reservations)').run();
      const insert=this.db.prepare(`INSERT INTO booking_slots(slot_id,location_code,session_type_id,starts_at,ends_at,catalog_revision,published_at) VALUES(?,?,?,?,?,?,?)
        ON CONFLICT(slot_id) DO UPDATE SET location_code=excluded.location_code,session_type_id=excluded.session_type_id,starts_at=excluded.starts_at,ends_at=excluded.ends_at,catalog_revision=excluded.catalog_revision,published_at=excluded.published_at
        WHERE NOT EXISTS(SELECT 1 FROM booking_slot_reservations WHERE slot_id=excluded.slot_id)`);
      const publishedAt=this.now().toISOString();for(const x of slots)insert.run(x.id,x.locationCode,x.sessionTypeId,x.startsAt,x.endsAt,x.catalogRevision,publishedAt);
      return {count:slots.length,catalogRevision:catalog.revision,publishedAt};
    };
    return alreadyInTransaction?save():this.transaction(save);
  }
  getBookingAvailability({from,to,locationCode,sessionTypeId}) {
    const fromMs=Date.parse(`${from}T00:00:00Z`),toMs=Date.parse(`${to}T00:00:00Z`);
    if(!/^\d{4}-\d{2}-\d{2}$/.test(from||'')||!/^\d{4}-\d{2}-\d{2}$/.test(to||'')||!Number.isFinite(fromMs)||!Number.isFinite(toMs)||new Date(fromMs).toISOString().slice(0,10)!==from||new Date(toMs).toISOString().slice(0,10)!==to||toMs<fromMs||toMs-fromMs>92*86400000||!['BK','BE'].includes(locationCode))fail(400,'invalid_availability_query','Provide a valid date range of no more than 93 days and a Studio location.');
    const catalog=this.getBookingCatalog();if(sessionTypeId&&!catalog.sessionTypes.some(x=>x.id===sessionTypeId&&x.active&&x.locations.includes(locationCode)))fail(400,'invalid_session','Choose an active session available at this location.');
    return {catalogRevision:catalog.revision,slots:this.db.prepare(`SELECT s.slot_id AS id,s.location_code AS locationCode,s.session_type_id AS sessionTypeId,s.starts_at AS startsAt,s.ends_at AS endsAt,s.catalog_revision AS catalogRevision
      FROM booking_slots s LEFT JOIN booking_slot_reservations r ON r.slot_id=s.slot_id WHERE r.slot_id IS NULL AND s.catalog_revision=? AND s.starts_at>? AND substr(s.starts_at,1,10)>=? AND substr(s.starts_at,1,10)<=? AND s.location_code=? AND (?='' OR s.session_type_id=?) ORDER BY s.starts_at`).all(catalog.revision,this.now().toISOString(),from,to,locationCode,sessionTypeId||'',sessionTypeId||'')};
  }
  authenticateApiCredential(secret, scope) {
    const hash=createHash('sha256').update(String(secret||'')).digest('hex');
    const row=this.db.prepare('SELECT * FROM api_credentials WHERE secret_hash=? AND revoked_at IS NULL').get(hash);
    if(!row)return null;
    const scopes=JSON.parse(row.scopes);if(!scopes.includes(scope))fail(403,'wrong_scope','Credential lacks the required scope.');
    this.db.prepare('UPDATE api_credentials SET last_used_at=? WHERE id=?').run(this.now().toISOString(),row.id);
    return row.id;
  }
  apiClientApplication(id) { return this.db.prepare('SELECT client_application FROM api_credentials WHERE id=? AND revoked_at IS NULL').get(id)?.client_application||null; }
  createClientLinkChallenge(clientId, ttlMinutes = 15) {
    if(!/^[0-9a-f-]{36}$/i.test(String(clientId||''))) fail(400,'invalid_client_id','A Studio client UUID is required.');
    const id=randomUUID(),code=randomBytes(24).toString('base64url'),created=this.now(),expires=new Date(created.getTime()+ttlMinutes*60000).toISOString();
    this.db.prepare('INSERT INTO client_link_challenges(id,code_hash,client_id,expires_at,created_at) VALUES(?,?,?,?,?)').run(id,createHash('sha256').update(code).digest('hex'),clientId,expires,created.toISOString());
    return {id,code,expiresAt:expires};
  }
  redeemClientLinkChallenge(code,provider,subject) {
    if(typeof code!=='string'||code.length<20||!['Google','Facebook','Apple'].includes(provider)||typeof subject!=='string'||subject.length<1||subject.length>300) fail(400,'invalid_link_proof','A valid provider and stable authenticated provider subject are required.');
    return this.transaction(()=>{
      const row=this.db.prepare('SELECT * FROM client_link_challenges WHERE code_hash=?').get(createHash('sha256').update(code).digest('hex'));
      if(!row||row.used_at||row.revoked_at||Date.parse(row.expires_at)<=this.now().getTime()) fail(404,'link_proof_unavailable','Link proof is invalid, expired, used, or revoked.');
      const existing=this.db.prepare('SELECT client_id FROM client_identity_links WHERE provider=? AND subject=? AND revoked_at IS NULL').get(provider,subject);
      if(existing&&existing.client_id!==row.client_id) fail(409,'identity_already_linked','This sign-in identity is already linked to another client.');
      this.db.prepare('UPDATE client_link_challenges SET used_at=?,provider=? WHERE id=? AND used_at IS NULL AND revoked_at IS NULL').run(this.now().toISOString(),provider,row.id);
      this.db.prepare('INSERT INTO client_identity_links(provider,subject,client_id,verified_at) VALUES(?,?,?,?) ON CONFLICT(provider,subject) DO UPDATE SET client_id=excluded.client_id,verified_at=excluded.verified_at,revoked_at=NULL').run(provider,subject,row.client_id,this.now().toISOString());
      return {clientId:row.client_id,provider,verifiedAt:this.now().toISOString()};
    });
  }
  clientForIdentity(provider,subject) { return this.db.prepare('SELECT client_id FROM client_identity_links WHERE provider=? AND subject=? AND revoked_at IS NULL').get(provider,subject)?.client_id||null; }
  clientLinkProofs(clientId) {
    const rows=this.db.prepare('SELECT id,client_id,created_at,expires_at,used_at,revoked_at,provider FROM client_link_challenges WHERE (? IS NULL OR client_id=?) ORDER BY created_at DESC').all(clientId||null,clientId||null);
    return rows.map(r=>({id:r.id,clientId:r.client_id,createdAt:r.created_at,expiresAt:r.expires_at,usedAt:r.used_at||null,revokedAt:r.revoked_at||null,provider:r.provider||null,status:r.revoked_at?'Revoked':r.used_at?'Used':Date.parse(r.expires_at)<=this.now().getTime()?'Expired':'Pending'}));
  }
  clientLinkProof(id) { return this.clientLinkProofs(null).find(x=>x.id===id)||null; }
  revokeClientLinkProof(id) {
    const r=this.db.prepare('UPDATE client_link_challenges SET revoked_at=? WHERE id=? AND used_at IS NULL AND revoked_at IS NULL AND expires_at>?').run(this.now().toISOString(),id,this.now().toISOString());
    return r.changes>0;
  }
  revokeClientIdentity(provider,subject) { return this.db.prepare('UPDATE client_identity_links SET revoked_at=? WHERE provider=? AND subject=? AND revoked_at IS NULL').run(this.now().toISOString(),provider,subject).changes>0; }
  replay(owner, key, payload, action) {
    if (!/^[A-Za-z0-9._-]{1,100}$/.test(key || '')) fail(400, 'idempotency_required', 'Provide Idempotency-Key (1–100 letters, digits, dots, underscores or dashes).');
    return this.transaction(() => {
      const hash = fingerprint(payload), prior = this.db.prepare('SELECT hash,result FROM replays WHERE owner=? AND key=?').get(owner, key);
      if (prior) { if (hash !== prior.hash) fail(409, 'idempotency_conflict', 'The key was used with different content.'); return JSON.parse(prior.result); }
      const result = action(); this.db.prepare('INSERT INTO replays VALUES(?,?,?,?)').run(owner, key, hash, JSON.stringify(result)); return result;
    });
  }
  create(owner, key, input, method = 'Api', clientApplication='API') {
    return this.replay(owner, key, input, () => {
      const kind = input.kind || 'Booking'; if (!['Inquiry','Booking'].includes(kind)) fail(400, 'invalid_kind', 'Choose Inquiry or Booking.');
      const p = { kind, source: method==='Form'?'Web site':clientApplication==='Workday Mobile'?'Workday Mobile':'API', submissionMethod: method, externalSource: owner };
      const limits={clientName:200,clientEmail:320,clientPhone:80,sessionType:200,sessionTypeId:100,slotId:100,locationCode:2,startsAt:50,endsAt:50,timeZone:100,location:500,notes:4000,destination:200,externalReference:200};
      for (const name of ['clientName','clientEmail','clientPhone','sessionType','sessionTypeId','slotId','locationCode','startsAt','endsAt','timeZone','location','notes','destination','externalReference']) {
        if (input[name] !== undefined && (typeof input[name] !== 'string' || input[name].length > limits[name])) fail(400, 'invalid_field', 'Invalid text field.', { [name]: 'Invalid type or length.' });
        p[name] = input[name]?.trim() || '';
      }
      const suppliedCatalogRevision=input.catalogRevision;p.catalogRevision=Number.isSafeInteger(suppliedCatalogRevision)?String(suppliedCatalogRevision):typeof suppliedCatalogRevision==='string'&&/^\d{1,16}$/.test(suppliedCatalogRevision)?suppliedCatalogRevision:'';
      if (!p.clientName) fail(400, 'invalid_field', 'Client name is required.', { clientName: 'Required.' });
      if (p.clientEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(p.clientEmail)) fail(400, 'invalid_field', 'Valid email required.', { clientEmail: 'Invalid email.' });
      p.timeZone ||= 'America/Jamaica'; try { new Intl.DateTimeFormat('en', { timeZone: p.timeZone }); } catch { fail(400,'invalid_timezone','Use an IANA timezone.'); }
      if (kind === 'Booking') {
        if (!p.sessionTypeId || !/^[1-9]\d*$/.test(p.catalogRevision)||!p.slotId||!['BK','BE'].includes(p.locationCode)||!p.clientEmail)fail(400,'invalid_booking','Choose a published Studio session and available time, and provide an email address.');
        const catalog=this.getBookingCatalog(),session=catalog.revision===Number(p.catalogRevision)&&catalog.sessionTypes.find(x=>x.id===p.sessionTypeId&&x.active);
        if(!session)fail(409,'catalog_changed','Studio session choices changed. Reload the booking form and try again.');
        if(!session.locations.includes(p.locationCode))fail(400,'invalid_booking_location','That Studio session is not available at the selected location.');
        const slot=this.db.prepare('SELECT * FROM booking_slots WHERE slot_id=?').get(p.slotId);
        if(!slot||slot.catalog_revision!==catalog.revision||slot.location_code!==p.locationCode||slot.session_type_id!==session.id)fail(409,'slot_unavailable','That time is no longer available. Choose another time.');
        p.startsAt=slot.starts_at;p.endsAt=slot.ends_at;
        p.sessionType=session.name;p.catalogRevision=catalog.revision;p.pricingSnapshot={sessionTypeId:session.id,sessionType:session.name,catalogRevision:catalog.revision,priceMinor:session.priceMinor,depositMinor:session.depositMinor,currency:session.currency,resourceRequirements:session.resourceRequirements,setupBufferMinutes:session.setupBufferMinutes,cleanupBufferMinutes:session.cleanupBufferMinutes};
      }
      if ((p.startsAt || p.endsAt) && (!/T.*(?:Z|[+-]\d\d:\d\d)$/.test(p.startsAt) || !/T.*(?:Z|[+-]\d\d:\d\d)$/.test(p.endsAt) || !Number.isFinite(Date.parse(p.startsAt)) || !(Date.parse(p.endsAt) > Date.parse(p.startsAt)))) fail(400,'invalid_schedule','Use ISO timestamps with offsets and end after start.');
      // Reject financial claims rather than accidentally treating them as verification.
      if (['payment','payments','paid','paymentStatus','confirmed','requiredDeposit'].some(k => k in input)) fail(400,'untrusted_payment','Payment and confirmation fields are not accepted on intake.');
      p.roster = validateRoster(input.roster); p.attachments = []; p.paymentEvents = [];
      const now = this.now(), year = Number(new Intl.DateTimeFormat('en', { timeZone: 'America/Jamaica', year: 'numeric' }).format(now));
      const referencePrefix = kind === 'Booking' ? p.locationCode : 'W';
      const next = this.db.prepare('INSERT INTO counters VALUES(?,?,1) ON CONFLICT(year,source) DO UPDATE SET value=value+1 RETURNING value').get(year,referencePrefix).value;
      if (next > 999999) fail(503,'reference_exhausted','Intake reference capacity exhausted. Contact staff.');
      p.id = randomUUID(); p.intakeReference = `${year}-${referencePrefix}-${String(next).padStart(6,'0')}`; p.createdAtUtc = now.toISOString(); p.revision = 1;
      const token = randomBytes(32).toString('base64url');
      this.db.prepare('INSERT INTO intakes(id,reference,owner,payload,revision,created_at,access_hash) VALUES(?,?,?,?,?,?,?)').run(p.id,p.intakeReference,owner,JSON.stringify(p),1,p.createdAtUtc,fingerprint(token));
      if(kind==='Booking'){
        try{this.db.prepare('INSERT INTO booking_slot_reservations(slot_id,intake_id,reserved_at) VALUES(?,?,?)').run(p.slotId,p.id,p.createdAtUtc);}
        catch(e){if(String(e.message).includes('UNIQUE'))fail(409,'slot_unavailable','That time was just booked. Choose another time.');throw e;}
      }
      this.change(p);
      return { id:p.id,reference:p.id,intakeReference:p.intakeReference,status:'received',importStatus:'Pending',createdAtUtc:p.createdAtUtc,accessToken:token,requiredDepositMinor:p.pricingSnapshot?.depositMinor||0,currency:'JMD',capacityWarning:next>=950000 };
    });
  }
  change(p) { this.db.prepare('INSERT INTO changes(intake_id,revision,payload) VALUES(?,?,?)').run(p.id,p.revision,JSON.stringify(p)); }
  get(id, owner, token) {
    const r = this.db.prepare('SELECT * FROM intakes WHERE id=? OR reference=?').get(id,id);
    if (!r || owner !== r.owner && fingerprint(token || '') !== r.access_hash) fail(404,'not_found','Intake not found.');
    return r;
  }
  status(id,owner,token) { const r=this.get(id,owner,token); return { id:r.id,intakeReference:r.reference,createdAtUtc:r.created_at,receiptStatus:'Received',importStatus:r.ack_revision ? 'Imported':'Pending',pendingChanges:r.ack_revision<r.revision,...JSON.parse(r.status) }; }
  feed(cursor = '', limit = 50) {
    let after = 0; try { if (cursor) { after = Number(Buffer.from(cursor,'base64url').toString()); if (!Number.isSafeInteger(after) || after<0) throw Error(); } } catch { fail(400,'invalid_cursor','Invalid change cursor.'); }
    const rows=this.db.prepare('SELECT sequence,payload FROM changes WHERE sequence>? ORDER BY sequence LIMIT ?').all(after,limit);
    return {items:rows.map(r=>JSON.parse(r.payload)),nextCursor:Buffer.from(String(rows.at(-1)?.sequence || after)).toString('base64url')};
  }
  ack(id,revision,status = {}) {
    return this.transaction(()=>{
      const r=this.db.prepare('SELECT * FROM intakes WHERE id=?').get(id); if(!r) fail(404,'not_found','Intake not found.');
      if(!Number.isInteger(revision)||revision<1||revision>r.revision) fail(409,'revision_conflict','Invalid imported revision.');
      if(revision>=r.ack_revision) this.db.prepare('UPDATE intakes SET ack_revision=?,status=? WHERE id=?').run(revision,JSON.stringify({...JSON.parse(r.status),...status}),id);
      if(revision>=r.ack_revision&&status.bookingStatus==='Cancelled')this.db.prepare('DELETE FROM booking_slot_reservations WHERE intake_id=?').run(id);
      return {id,acknowledgedRevision:Math.max(revision,r.ack_revision),currentRevision:r.revision};
    });
  }
  roster(id,owner,token,revision,rows) {
    return this.transaction(()=>{
      const r=this.get(id,owner,token); if(r.revision!==revision) fail(409,'revision_conflict','Reload the current roster before saving.');
      const p=JSON.parse(r.payload), validated=validateRoster(rows); p.roster=validated;p.revision++;
      this.db.prepare('UPDATE intakes SET payload=?,revision=? WHERE id=?').run(JSON.stringify(p),p.revision,id);this.change(p);return p;
    });
  }
  payment(id,event) {
    return this.transaction(()=>{
      const r=this.db.prepare('SELECT payload FROM intakes WHERE id=?').get(id);if(!r)fail(404,'not_found','Intake not found.');
      if(JSON.parse(r.payload).kind!=='Booking')fail(409,'booking_required','Verified payment events require a booking.');
      if(!event.eventId||!event.transactionReference||!event.provider||!['Deposit','Refund','Dispute'].includes(event.kind)||event.currency!=='JMD'||!Number.isSafeInteger(event.amountMinor)||event.amountMinor<=0||event.amountMinor>99_999_999_900)fail(400,'invalid_payment','Verified event ID, provider, reference, kind and positive minor-unit JMD amount required.');
      const hash=fingerprint(event), old=this.db.prepare('SELECT hash,intake_id FROM payment_events WHERE id=?').get(event.eventId);
      if(old){if(old.hash!==hash||old.intake_id!==id)fail(409,'payment_conflict','Event ID reused with different facts.');return {eventId:event.eventId};}
      this.db.prepare('INSERT INTO payment_events VALUES(?,?,?,?)').run(event.eventId,id,hash,JSON.stringify(event));
      const p=JSON.parse(r.payload);p.paymentEvents.push(event);p.revision++;this.db.prepare('UPDATE intakes SET payload=?,revision=? WHERE id=?').run(JSON.stringify(p),p.revision,id);this.change(p);return {eventId:event.eventId};
    });
  }
  close() { this.db.close(); }
}


