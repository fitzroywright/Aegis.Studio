import { randomUUID } from 'node:crypto';

export function ensureActivity(db) {
  db.exec(`CREATE TABLE IF NOT EXISTS exchange_activity(
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,aggregate_id TEXT NOT NULL,
    owner TEXT NOT NULL,action TEXT NOT NULL,actor TEXT NOT NULL,occurred_at TEXT NOT NULL,
    recorded_at TEXT NOT NULL,details TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_exchange_activity_owner ON exchange_activity(owner,aggregate_id,sequence);
    CREATE TRIGGER IF NOT EXISTS exchange_activity_no_update BEFORE UPDATE ON exchange_activity
      BEGIN SELECT RAISE(ABORT,'Activity history is append only'); END;
    CREATE TRIGGER IF NOT EXISTS exchange_activity_no_delete BEFORE DELETE ON exchange_activity
      BEGIN SELECT RAISE(ABORT,'Activity history is append only'); END;`);
}

// Call inside the same transaction as the state change. Never pass request objects or credentials.
export function recordActivity(db, owner, aggregateId, action, actor, details = {}, occurredAt = null) {
  const now = new Date().toISOString(), id = randomUUID();
  db.prepare('INSERT INTO exchange_activity(id,aggregate_id,owner,action,actor,occurred_at,recorded_at,details) VALUES(?,?,?,?,?,?,?,?)')
    .run(id, aggregateId, owner, action, actor, occurredAt ? new Date(occurredAt).toISOString() : now, now, JSON.stringify(details));
}

export function activityFor(db, owner, aggregateId, after = 0) {
  return db.prepare('SELECT sequence,id,aggregate_id AS aggregateId,action,actor,occurred_at AS occurredAt,recorded_at AS recordedAt,details FROM exchange_activity WHERE owner=? AND aggregate_id=? AND sequence>? ORDER BY sequence LIMIT 500')
    .all(owner, aggregateId, after).map(x => ({ ...x, details: JSON.parse(x.details) }));
}

export function canonicalJson(value) {
  function ordered(v){return Array.isArray(v)?v.map(ordered):v&&typeof v==='object'?Object.fromEntries(Object.keys(v).sort().map(k=>[k,ordered(v[k])])):v;}
  return JSON.stringify(ordered(value));
}
