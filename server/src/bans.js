// Ban list in SQLite. Stores SHA-256(clientId) only — never an IP, never a name.
import { createHash } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export function hashClientId(clientId) {
  return createHash('sha256').update(String(clientId).trim().toLowerCase()).digest('hex');
}

// path may be ':memory:' (tests) or a file path (directory created on demand).
export function openBans(path) {
  if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
  const db = new DatabaseSync(path);
  db.exec('PRAGMA journal_mode = WAL');
  db.exec('CREATE TABLE IF NOT EXISTS bans (hash TEXT PRIMARY KEY, created_at INTEGER NOT NULL)');
  const q = {
    get: db.prepare('SELECT 1 FROM bans WHERE hash = ?'),
    put: db.prepare('INSERT OR IGNORE INTO bans (hash, created_at) VALUES (?, ?)'),
    del: db.prepare('DELETE FROM bans WHERE hash = ?'),
    all: db.prepare('SELECT hash, created_at FROM bans ORDER BY created_at'),
  };
  return {
    isBanned: (clientId) => q.get.get(hashClientId(clientId)) !== undefined,
    ban: (clientId) => q.put.run(hashClientId(clientId), Date.now()).changes > 0,
    unban: (clientId) => q.del.run(hashClientId(clientId)).changes > 0,
    list: () => q.all.all(),
    close: () => db.close(),
  };
}
