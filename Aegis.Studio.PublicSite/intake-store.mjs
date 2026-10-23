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
      CREATE TABLE IF NOT EXISTS api_credentials(id TEXT PRIMARY KEY,name TEXT NOT NULL,secret_hash TEXT NOT NULL UNIQUE,scopes TEXT NOT NULL,created_at TEXT NOT NULL,last_used_at TEXT,revoked_at TEXT);
      CREATE TABLE IF NOT EXISTS client_link_challenges(id TEXT PRIMARY KEY,code_hash TEXT NOT NULL UNIQUE,client_id TEXT NOT NULL,expires_at TEXT NOT NULL,used_at TEXT,revoked_at TEXT,created_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS client_identity_links(provider TEXT NOT NULL,subject TEXT NOT NULL,client_id TEXT NOT NULL,verified_at TEXT NOT NULL,revoked_at TEXT,PRIMARY KEY(provider,subject));
      CREATE TABLE IF NOT EXISTS media(gallery_id TEXT,id TEXT,type TEXT,content BLOB,PRIMARY KEY(gallery_id,id));
      CREATE TABLE IF NOT EXISTS selections(gallery_id TEXT,id TEXT,selected INTEGER,PRIMARY KEY(gallery_id,id));
      CREATE TABLE IF NOT EXISTS payment_events(id TEXT PRIMARY KEY,intake_id TEXT NOT NULL,hash TEXT NOT NULL,payload TEXT NOT NULL);`);
  }
  transaction(action) { this.db.exec('BEGIN IMMEDIATE'); try { const result = action(); this.db.exec('COMMIT'); return result; } catch (e) { this.db.exec('ROLLBACK'); throw e; } }
  createApiCredential(name, scopes) {
    if(typeof name!=='string'||!name.trim()||name.trim().length>100) fail(400,'invalid_key_name','Provide a key name up to 100 characters.');
    const allowed=new Set(['intake:submit','intake:pull','intake:ack','bookings:read','availability:read','gallery:publish','gallery:read','client:link','attachment:scan','payment:verified']);
    if(!Array.isArray(scopes)||!scopes.length||scopes.some(x=>!allowed.has(x))||new Set(scopes).size!==scopes.length) fail(400,'invalid_scopes','Choose one or more supported, unique scopes.');
    const id=randomUUID(),secret=randomBytes(32).toString('base64url'),createdAt=this.now().toISOString();
    this.db.prepare('INSERT INTO api_credentials(id,name,secret_hash,scopes,created_at) VALUES(?,?,?,?,?)').run(id,name.trim(),createHash('sha256').update(secret).digest('hex'),JSON.stringify(scopes),createdAt);
    return {id,name:name.trim(),scopes,createdAt,secret};
  }
  listApiCredentials() {
    return this.db.prepare('SELECT id,name,scopes,created_at,last_used_at,revoked_at FROM api_credentials ORDER BY created_at DESC').all().map(x=>({...x,scopes:JSON.parse(x.scopes),status:x.revoked_at?'Revoked':'Active'}));
  }
  revokeApiCredential(id) {
    const result=this.db.prepare('UPDATE api_credentials SET revoked_at=COALESCE(revoked_at,?) WHERE id=?').run(this.now().toISOString(),id);
    return result.changes>0;
  }
  authenticateApiCredential(secret, scope) {
    const hash=createHash('sha256').update(String(secret||'')).digest('hex');
    const row=this.db.prepare('SELECT * FROM api_credentials WHERE secret_hash=? AND revoked_at IS NULL').get(hash);
    if(!row)return null;
    const scopes=JSON.parse(row.scopes);if(!scopes.includes(scope))fail(403,'wrong_scope','Credential lacks the required scope.');
    this.db.prepare('UPDATE api_credentials SET last_used_at=? WHERE id=?').run(this.now().toISOString(),row.id);
    return row.id;
  }
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
      this.db.prepare('UPDATE client_link_challenges SET used_at=? WHERE id=? AND used_at IS NULL AND revoked_at IS NULL').run(this.now().toISOString(),row.id);
      this.db.prepare('INSERT INTO client_identity_links(provider,subject,client_id,verified_at) VALUES(?,?,?,?) ON CONFLICT(provider,subject) DO UPDATE SET client_id=excluded.client_id,verified_at=excluded.verified_at,revoked_at=NULL').run(provider,subject,row.client_id,this.now().toISOString());
      return {clientId:row.client_id,provider,verifiedAt:this.now().toISOString()};
    });
  }
  clientForIdentity(provider,subject) { return this.db.prepare('SELECT client_id FROM client_identity_links WHERE provider=? AND subject=? AND revoked_at IS NULL').get(provider,subject)?.client_id||null; }
  revokeClientIdentity(provider,subject) { return this.db.prepare('UPDATE client_identity_links SET revoked_at=? WHERE provider=? AND subject=? AND revoked_at IS NULL').run(this.now().toISOString(),provider,subject).changes>0; }
  replay(owner, key, payload, action) {
    if (!/^[A-Za-z0-9._-]{1,100}$/.test(key || '')) fail(400, 'idempotency_required', 'Provide Idempotency-Key (1–100 letters, digits, dots, underscores or dashes).');
    return this.transaction(() => {
      const hash = fingerprint(payload), prior = this.db.prepare('SELECT hash,result FROM replays WHERE owner=? AND key=?').get(owner, key);
      if (prior) { if (hash !== prior.hash) fail(409, 'idempotency_conflict', 'The key was used with different content.'); return JSON.parse(prior.result); }
      const result = action(); this.db.prepare('INSERT INTO replays VALUES(?,?,?,?)').run(owner, key, hash, JSON.stringify(result)); return result;
    });
  }
  create(owner, key, input, method = 'Api') {
    return this.replay(owner, key, input, () => {
      const kind = input.kind || 'Booking'; if (!['Inquiry','Booking'].includes(kind)) fail(400, 'invalid_kind', 'Choose Inquiry or Booking.');
      const p = { kind, source: 'Website', submissionMethod: method, externalSource: owner };
      const limits={clientName:200,clientEmail:320,clientPhone:80,sessionType:200,locationCode:2,startsAt:50,endsAt:50,timeZone:100,location:500,notes:4000,destination:200,externalReference:200};
      for (const name of ['clientName','clientEmail','clientPhone','sessionType','locationCode','startsAt','endsAt','timeZone','location','notes','destination','externalReference']) {
        if (input[name] !== undefined && (typeof input[name] !== 'string' || input[name].length > limits[name])) fail(400, 'invalid_field', 'Invalid text field.', { [name]: 'Invalid type or length.' });
        p[name] = input[name]?.trim() || '';
      }
      if (!p.clientName) fail(400, 'invalid_field', 'Client name is required.', { clientName: 'Required.' });
      if (p.clientEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(p.clientEmail)) fail(400, 'invalid_field', 'Valid email required.', { clientEmail: 'Invalid email.' });
      p.timeZone ||= 'America/Jamaica'; try { new Intl.DateTimeFormat('en', { timeZone: p.timeZone }); } catch { fail(400,'invalid_timezone','Use an IANA timezone.'); }
      if (kind === 'Booking' && (!p.sessionType || !['BK','BE'].includes(p.locationCode) || !p.startsAt || !p.endsAt || !p.clientEmail)) fail(400,'invalid_booking','Session, location, email and session times are required.', { locationCode: 'Choose Kings Plaza (BK) or East Queen Street (BE).' });
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
      this.change(p);
      return { id:p.id,reference:p.id,intakeReference:p.intakeReference,status:'received',importStatus:'Pending',createdAtUtc:p.createdAtUtc,accessToken:token,requiredDeposit:0,currency:'JMD',capacityWarning:next>=950000 };
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

