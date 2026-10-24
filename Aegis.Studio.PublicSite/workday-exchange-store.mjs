import { DatabaseSync } from 'node:sqlite';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { ensureActivity, recordActivity } from './exchange-activity.mjs';
import { createHash, randomBytes, randomUUID, scryptSync, timingSafeEqual } from 'node:crypto';

const hash = value => createHash('sha256').update(String(value)).digest('hex');
const json = value => JSON.stringify(value);
const safeEqual = (a,b) => { const x=Buffer.from(a||''),y=Buffer.from(b||''); return x.length===y.length&&timingSafeEqual(x,y); };

export class WorkDayExchangeStore {
  constructor(path, now=()=>new Date(), env={}) {
    if(path!==':memory:')mkdirSync(dirname(path),{recursive:true,mode:0o700});
    this.db=new DatabaseSync(path);this.now=now;
    this.offlineHours=Math.min(24,Math.max(1,Number(env.WORKDAY_AUTHORIZATION_MAX_AGE_HOURS)||24));
    ensureActivity(this.db);
    this.db.exec(`PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;
      CREATE TABLE IF NOT EXISTS workday_accounts(user_key TEXT PRIMARY KEY,user_name TEXT NOT NULL,display_name TEXT NOT NULL,active INTEGER NOT NULL DEFAULT 1,enrollment_hash TEXT,enrollment_expires TEXT,password_salt TEXT,password_hash TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS workday_sessions(token_hash TEXT PRIMARY KEY,user_key TEXT NOT NULL REFERENCES workday_accounts(user_key),device_id TEXT,created_at TEXT NOT NULL,expires_at TEXT NOT NULL,revoked_at TEXT);
      CREATE INDEX IF NOT EXISTS ix_workday_sessions_user ON workday_sessions(user_key,expires_at);
      CREATE TABLE IF NOT EXISTS workday_snapshots(user_key TEXT PRIMARY KEY REFERENCES workday_accounts(user_key),revision INTEGER NOT NULL,payload TEXT NOT NULL,updated_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS workday_updates(sequence INTEGER PRIMARY KEY AUTOINCREMENT,operation_id TEXT NOT NULL UNIQUE,user_key TEXT NOT NULL REFERENCES workday_accounts(user_key),kind TEXT NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'Pending',result TEXT,created_at TEXT NOT NULL,processed_at TEXT);
      CREATE INDEX IF NOT EXISTS ix_workday_updates_pending ON workday_updates(state,sequence);
      CREATE TABLE IF NOT EXISTS workday_client_observations(device_id TEXT PRIMARY KEY,user_key TEXT NOT NULL,payload TEXT NOT NULL,observed_at TEXT NOT NULL); `);
  }
  transaction(action){this.db.exec('BEGIN IMMEDIATE');try{const value=action();this.db.exec('COMMIT');return value;}catch(error){this.db.exec('ROLLBACK');throw error;}}
  provisionAccount(input) {
    if(!input||typeof input.userName!=='string'||!input.userName.trim()||input.userName.length>200||typeof input.displayName!=='string'||input.displayName.length>200)throw Object.assign(new Error('A user name and display name are required.'),{status:400,code:'invalid_account'});
    const userKey=input.userName.trim().toLowerCase(),userName=input.userName.trim(),displayName=input.displayName.trim();
    const existing=this.db.prepare('SELECT password_hash,enrollment_expires FROM workday_accounts WHERE user_key=?').get(userKey),now=this.now();
    if(existing){
      this.db.prepare('UPDATE workday_accounts SET user_name=?,display_name=?,active=1,updated_at=? WHERE user_key=?').run(userName,displayName,now.toISOString(),userKey);
      if(existing.password_hash)return {userName,displayName,enrollmentRequired:false};
      if(existing.enrollment_expires&&Date.parse(existing.enrollment_expires)>now.getTime())
        return {userName,displayName,enrollmentRequired:true};
      const enrollmentToken=randomBytes(32).toString('base64url'),expires=new Date(now.getTime()+24*60*60*1000).toISOString();
      this.db.prepare('UPDATE workday_accounts SET enrollment_hash=?,enrollment_expires=?,updated_at=? WHERE user_key=?')
        .run(hash(enrollmentToken),expires,now.toISOString(),userKey);
      return {userName,displayName,enrollmentRequired:true,enrollmentToken,expiresAt:expires};
    }
    const enrollmentToken=randomBytes(32).toString('base64url'),expires=new Date(now.getTime()+24*60*60*1000).toISOString();
    this.db.prepare('INSERT INTO workday_accounts(user_key,user_name,display_name,active,enrollment_hash,enrollment_expires,created_at,updated_at) VALUES(?,?,?,1,?,?,?,?)').run(userKey,userName,displayName,hash(enrollmentToken),expires,now.toISOString(),now.toISOString());
    return {userName,displayName,enrollmentRequired:true,enrollmentToken,expiresAt:expires};
  }
  enroll(token,password) {
    if(typeof token!=='string'||token.length<32||typeof password!=='string'||password.length<12||password.length>200)throw Object.assign(new Error('Use a valid enrollment code and a password of at least 12 characters.'),{status:400,code:'invalid_enrollment'});
    const row=this.db.prepare('SELECT * FROM workday_accounts WHERE enrollment_hash=? AND active=1').get(hash(token));
    if(!row||!row.enrollment_expires||Date.parse(row.enrollment_expires)<=this.now().getTime())throw Object.assign(new Error('Enrollment code is invalid or expired.'),{status:404,code:'enrollment_unavailable'});
    const salt=randomBytes(16),derived=scryptSync(password,salt,64),now=this.now().toISOString();
    this.db.prepare('UPDATE workday_accounts SET password_salt=?,password_hash=?,enrollment_hash=NULL,enrollment_expires=NULL,updated_at=? WHERE user_key=?').run(salt.toString('base64url'),derived.toString('base64url'),now,row.user_key);
    return {userName:row.user_name,displayName:row.display_name};
  }
  login(userName,password,deviceId='') {
    const row=this.db.prepare('SELECT * FROM workday_accounts WHERE user_key=? AND active=1').get(String(userName||'').trim().toLowerCase());
    if(!row||!row.password_salt||!row.password_hash)throw Object.assign(new Error('Authentication failed.'),{status:401,code:'unauthorized'});
    const derived=scryptSync(String(password||''),Buffer.from(row.password_salt,'base64url'),64);
    if(!safeEqual(derived,Buffer.from(row.password_hash,'base64url')))throw Object.assign(new Error('Authentication failed.'),{status:401,code:'unauthorized'});
    const token=randomBytes(32).toString('base64url'),now=this.now(),expires=new Date(now.getTime()+8*60*60*1000).toISOString();
    this.db.prepare('INSERT INTO workday_sessions(token_hash,user_key,device_id,created_at,expires_at) VALUES(?,?,?,?,?)').run(hash(token),row.user_key,String(deviceId||'').slice(0,200),now.toISOString(),expires);
    return {token,userName:row.user_name,displayName:row.display_name,expiresAt:expires};
  }
  authenticate(token) {
    const row=this.db.prepare(`SELECT a.user_key,a.user_name,a.display_name,s.device_id FROM workday_sessions s JOIN workday_accounts a USING(user_key)
      WHERE s.token_hash=? AND s.revoked_at IS NULL AND s.expires_at>? AND a.active=1 AND a.updated_at>?`).get(hash(token),this.now().toISOString(),new Date(this.now().getTime()-this.offlineHours*3600000).toISOString());
    return row||null;
  }
  issueDeviceSession(userName,deviceId) {
    const key=String(userName||'').trim().toLowerCase(),account=this.db.prepare('SELECT user_name,display_name FROM workday_accounts WHERE user_key=? AND active=1').get(key);
    if(!account)throw Object.assign(new Error('WorkDay account is not active.'),{status:404,code:'account_not_found'});
    const token=randomBytes(32).toString('base64url'),now=this.now(),expires=new Date(now.getTime()+30*24*60*60*1000).toISOString();
    this.db.prepare('INSERT INTO workday_sessions(token_hash,user_key,device_id,created_at,expires_at) VALUES(?,?,?,?,?)').run(hash(token),key,String(deviceId||'').slice(0,200),now.toISOString(),expires);
    return {sessionId:randomUUID(),token,expiresAtUtc:expires,userName:account.user_name,displayName:account.display_name};
  }
  revokeDevice(userName,deviceId) {
    const key=String(userName||'').trim().toLowerCase();
    return this.db.prepare("UPDATE workday_sessions SET revoked_at=? WHERE user_key=? AND device_id=? AND revoked_at IS NULL").run(this.now().toISOString(),key,String(deviceId||'')).changes>0;
  }
  revoke(token) { return this.db.prepare('UPDATE workday_sessions SET revoked_at=? WHERE token_hash=? AND revoked_at IS NULL').run(this.now().toISOString(),hash(token)).changes>0; }
  publishSnapshot(userName,revision,payload) {
    const key=String(userName||'').trim().toLowerCase();
    if(!Number.isSafeInteger(revision)||revision<1||!payload||typeof payload!=='object')throw Object.assign(new Error('User, revision and snapshot payload are required.'),{status:400,code:'invalid_snapshot'});
    const account=this.db.prepare('SELECT user_key FROM workday_accounts WHERE user_key=? AND active=1').get(key);if(!account)throw Object.assign(new Error('WorkDay account is not provisioned.'),{status:404,code:'account_not_found'});
    const previous=this.db.prepare('SELECT revision FROM workday_snapshots WHERE user_key=?').get(key);
    if(previous&&revision<previous.revision)throw Object.assign(new Error('Snapshot revisions cannot move backwards.'),{status:409,code:'stale_snapshot'});
    const updatedAt=this.now().toISOString();
    this.db.prepare('INSERT INTO workday_snapshots VALUES(?,?,?,?) ON CONFLICT(user_key) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,updated_at=excluded.updated_at').run(key,revision,json(payload),updatedAt);
    return {userName,revision,updatedAt};
  }
  snapshot(userName) {
    const row=this.db.prepare('SELECT revision,payload,updated_at FROM workday_snapshots WHERE user_key=?').get(String(userName||'').toLowerCase());
    return row&&Date.parse(row.updated_at)>this.now().getTime()-this.offlineHours*3600000?{...JSON.parse(row.payload),revision:row.revision,updatedAt:row.updated_at}:null;
  }
  enqueue(userName,operationId,kind,payload) {
    if(!/^[0-9a-f-]{36}$/i.test(String(operationId||''))||typeof kind!=='string'||kind.length>80||!payload||typeof payload!=='object')throw Object.assign(new Error('A stable operation ID and valid payload are required.'),{status:400,code:'invalid_operation'});
    const key=String(userName||'').toLowerCase(),account=this.db.prepare('SELECT user_key FROM workday_accounts WHERE user_key=? AND active=1').get(key);
    if(!account)throw Object.assign(new Error('WorkDay account is not provisioned.'),{status:404,code:'account_not_found'});
    const encoded=json(payload),prior=this.db.prepare('SELECT user_key,kind,payload,state,result FROM workday_updates WHERE operation_id=?').get(operationId);
    if(prior){if(prior.user_key!==key||prior.kind!==kind||prior.payload!==encoded)throw Object.assign(new Error('Operation ID was already used for different data.'),{status:409,code:'operation_conflict'});return {operationId,state:prior.state,result:prior.result?JSON.parse(prior.result):null,replayed:true};}
    this.transaction(()=>{this.db.prepare('INSERT INTO workday_updates(operation_id,user_key,kind,payload,created_at) VALUES(?,?,?,?,?)').run(operationId,key,kind,encoded,this.now().toISOString());recordActivity(this.db,key,operationId,'WorkDayOperationReceived',key,{kind});});
    console.info(JSON.stringify({event:'WorkDayOperationReceived',operationId,kind}));
    return {operationId,state:'Pending',result:null,replayed:false};
  }
  pendingUpdates(after=0,limit=100,kind='') {
    return this.db.prepare(`SELECT sequence,operation_id AS operationId,user_key AS userName,kind,payload,created_at AS createdAt FROM workday_updates WHERE state='Pending' AND sequence>? AND (?='' OR kind=?) ORDER BY sequence LIMIT ?`).all(after,kind,kind,Math.max(1,Math.min(500,limit))).map(x=>({...x,payload:JSON.parse(x.payload)}));
  }
  completeUpdate(operationId,result) {
    const row=this.db.prepare('SELECT state,user_key,kind FROM workday_updates WHERE operation_id=?').get(operationId);if(!row)throw Object.assign(new Error('WorkDay update not found.'),{status:404,code:'not_found'});
    if(row.state!=='Pending')return {operationId,state:row.state,result:JSON.parse(this.db.prepare('SELECT result FROM workday_updates WHERE operation_id=?').get(operationId).result||'null')};
    const state=result?.state==='Conflict'?'Conflict':'Acknowledged';
    this.transaction(()=>{this.db.prepare('UPDATE workday_updates SET state=?,result=?,processed_at=? WHERE operation_id=?').run(state,json(result||{}),this.now().toISOString(),operationId);recordActivity(this.db,row.user_key,operationId,'WorkDayOperation'+state,'Studio',{kind:row.kind});});
    console.info(JSON.stringify({event:'WorkDayOperationResult',operationId,state}));
    return {operationId,state,result:result||{}};
  }
  updateStatus(userName,operationId) {
    const row=this.db.prepare('SELECT state,result,created_at FROM workday_updates WHERE user_key=? AND operation_id=?').get(String(userName||'').toLowerCase(),operationId);
    return row?{operationId,state:row.state,result:row.result?JSON.parse(row.result):null,createdAt:row.created_at}:null;
  }
  observe(userName,deviceId,payload) {
    this.db.prepare('INSERT INTO workday_client_observations VALUES(?,?,?,?) ON CONFLICT(device_id) DO UPDATE SET user_key=excluded.user_key,payload=excluded.payload,observed_at=excluded.observed_at').run(deviceId,String(userName||'').toLowerCase(),json(payload),this.now().toISOString());
  }
  reconcileAccounts(userNames) {
    if(!Array.isArray(userNames)||userNames.some(x=>typeof x!=='string'||x.length>200))throw Object.assign(new Error('A complete active user list is required.'),{status:400,code:'invalid_accounts'});
    const active=new Set(userNames.map(x=>x.trim().toLowerCase())),now=this.now().toISOString();let revoked=0;
    this.db.exec('BEGIN IMMEDIATE');try{
      for(const row of this.db.prepare('SELECT user_key FROM workday_accounts WHERE active=1').all())if(!active.has(row.user_key)){
        this.db.prepare('UPDATE workday_accounts SET active=0,updated_at=? WHERE user_key=?').run(now,row.user_key);
        this.db.prepare('UPDATE workday_sessions SET revoked_at=? WHERE user_key=? AND revoked_at IS NULL').run(now,row.user_key);
        recordActivity(this.db,row.user_key,row.user_key,'AccountRevoked','Studio');revoked++;
      }
      this.db.exec('COMMIT');return {revoked};
    }catch(error){this.db.exec('ROLLBACK');throw error;}
  }
  close(){this.db.close();}
}

