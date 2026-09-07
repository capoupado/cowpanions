#!/usr/bin/env node
// Moderation CLI. Operates on the same SQLite file the server uses; the server
// re-checks the ban list on every hello and on each eviction sweep, so a ban
// takes effect without a restart.
//
//   node bin/cowpanion-ban.js ban   <clientId>
//   node bin/cowpanion-ban.js unban <clientId>
//   node bin/cowpanion-ban.js list
//
// Data dir: $COWPANION_DATA (default ./data). Only SHA-256(clientId) is stored.
import path from 'node:path';
import { openBans, hashClientId } from '../src/bans.js';

const [cmd, arg] = process.argv.slice(2);
const dbPath = path.join(process.env.COWPANION_DATA ?? './data', 'bans.sqlite');
const ID_RE = /^[0-9a-f]{32}$/i;

function usage(code) {
  process.stderr.write('usage: cowpanion-ban.js ban <clientId> | unban <clientId> | list\n');
  process.exit(code);
}

if (!cmd || !['ban', 'unban', 'list'].includes(cmd)) usage(2);
if (cmd !== 'list' && !(arg && ID_RE.test(arg))) {
  process.stderr.write('clientId must be 32 hex characters\n');
  usage(2);
}

const bans = openBans(dbPath);
try {
  if (cmd === 'ban') {
    const added = bans.ban(arg);
    console.log(`${added ? 'banned' : 'already banned'} ${hashClientId(arg).slice(0, 12)}… (${dbPath})`);
  } else if (cmd === 'unban') {
    const removed = bans.unban(arg);
    console.log(`${removed ? 'unbanned' : 'not banned'} ${hashClientId(arg).slice(0, 12)}… (${dbPath})`);
  } else {
    const rows = bans.list();
    if (rows.length === 0) console.log(`no bans (${dbPath})`);
    for (const r of rows) console.log(`${r.hash}  ${new Date(r.created_at).toISOString()}`);
  }
} finally {
  bans.close();
}
