// End-to-end tests: real ws clients against createServer() on an ephemeral port.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { WebSocket } from 'ws';
import {
  startServer, connect, connectExpectReject, hello, join, waitFor, sendJson, sleep, cid, closeAll,
} from './helpers.js';

let S; // shared default server for most tests
before(async () => { S = await startServer(); });
after(async () => { await S.stop(); });

const presenceWith = (n) => (m) => m.t === 'presence' && m.members.length === n;
const presenceIds = (m) => m.members.map((x) => x.id).sort();

test('S1: three clients in one pasture each see all three in presence', async () => {
  const a = await join(S.port, { clientId: cid(1), pasture: 'trio', displayName: 'A' });
  const b = await join(S.port, { clientId: cid(2), pasture: 'trio', displayName: 'B' });
  const c = await join(S.port, { clientId: cid(3), pasture: 'trio', displayName: 'C' });
  for (const ws of [a, b, c]) {
    const p = await waitFor(ws, presenceWith(3));
    assert.deepEqual(presenceIds(p), [cid(1), cid(2), cid(3)]);
    assert.equal(p.overflow, 0);
    assert.deepEqual(p.members.find((m) => m.id === cid(2)), { id: cid(2), name: 'B', variant: 'brown' });
  }
  assert.equal(a.welcome.yourId, cid(1));
  assert.equal(a.welcome.pasture, 'trio');
  assert.equal(a.welcome.visibleCap, 12);
  assert.equal(a.welcome.protocolVersion, 2, 'helpers hello with the current version by default');
  assert.ok(Math.abs(a.welcome.serverTime - Date.now()) < 5000);
  closeAll([a, b, c]);
});

test('S1: a member leaving (socket drop) or sending bye is removed from presence', async () => {
  const a = await join(S.port, { clientId: cid(11), pasture: 'leave' });
  const b = await join(S.port, { clientId: cid(12), pasture: 'leave' });
  const c = await join(S.port, { clientId: cid(13), pasture: 'leave' });
  await waitFor(a, presenceWith(3));
  b.terminate(); // killed terminal
  const p = await waitFor(a, presenceWith(2));
  assert.deepEqual(presenceIds(p), [cid(11), cid(13)]);
  sendJson(c, { t: 'bye' });
  const p2 = await waitFor(a, presenceWith(1));
  assert.deepEqual(presenceIds(p2), [cid(11)]);
  assert.equal((await c.closed).code, 1000);
  closeAll([a]);
});

test('S1: silent client is evicted after the eviction window while a pinging one survives', async () => {
  const T = await startServer({ evictionMs: 400, sweepIntervalMs: 50 });
  try {
    const quiet = await join(T.port, { clientId: cid(21), pasture: 'evict' });
    const lively = await join(T.port, { clientId: cid(22), pasture: 'evict' });
    await waitFor(lively, presenceWith(2));
    const pinger = setInterval(() => sendJson(lively, { t: 'ping' }), 100);
    const closed = await quiet.closed;
    assert.equal(closed.code, 1000);
    const p = await waitFor(lively, presenceWith(1));
    assert.deepEqual(presenceIds(p), [cid(22)]);
    await sleep(500);
    assert.equal(lively.readyState, WebSocket.OPEN, 'pinging client not evicted');
    clearInterval(pinger);
    assert.ok((await waitFor(lively, (m) => m.t === 'pong')).t === 'pong');
    closeAll([lively]);
  } finally { await T.stop(); }
});

test('S1: unsolicited presence refresh arrives on the interval', async () => {
  const T = await startServer({ presenceIntervalMs: 150 });
  try {
    const a = await join(T.port, { clientId: cid(31), pasture: 'refresh' });
    await waitFor(a, presenceWith(1));
    await waitFor(a, presenceWith(1));
    await waitFor(a, presenceWith(1));
    closeAll([a]);
  } finally { await T.stop(); }
});

test('S1: same clientId connecting twice yields one member and closes the first socket', async () => {
  const first = await join(S.port, { clientId: cid(41), pasture: 'dup', displayName: 'old' });
  const watcher = await join(S.port, { clientId: cid(42), pasture: 'dup' });
  await waitFor(watcher, presenceWith(2));
  const second = await join(S.port, { clientId: cid(41), pasture: 'dup', displayName: 'new' });
  assert.equal((await first.closed).code, 1000);
  const p = await waitFor(second, presenceWith(2));
  assert.deepEqual(presenceIds(p), [cid(41), cid(42)]);
  assert.equal(p.members.find((m) => m.id === cid(41)).name, 'new');
  assert.equal(S.server.stats().members, S.server.stats().members); // sanity: no throw
  closeAll([second, watcher]);
});

test('S1: two pasture codes are fully isolated (presence and chat)', async () => {
  const a = await join(S.port, { clientId: cid(51), pasture: 'alpha' });
  const b = await join(S.port, { clientId: cid(52), pasture: 'beta' });
  const a2 = await join(S.port, { clientId: cid(53), pasture: 'alpha' });
  const pa = await waitFor(a, presenceWith(2));
  assert.deepEqual(presenceIds(pa), [cid(51), cid(53)]);
  sendJson(a, { t: 'chat', text: 'alpha only' });
  sendJson(b, { t: 'chat', text: 'beta only' });
  assert.equal((await waitFor(a2, (m) => m.t === 'chat')).text, 'alpha only');
  assert.equal((await waitFor(b, (m) => m.t === 'chat')).text, 'beta only');
  await sleep(150);
  assert.equal(b.inbox.filter((m) => m.t === 'chat' || (m.t === 'presence' && m.members.length !== 1)).length, 0);
  assert.equal(a2.inbox.filter((m) => m.t === 'chat').length, 0);
  closeAll([a, b, a2]);
});

test('S1: 25th member gets close 4001; 13 members see 12 entries and overflow 1', async () => {
  const members = [];
  for (let i = 1; i <= 13; i += 1) members.push(await join(S.port, { clientId: cid(100 + i), pasture: 'big' }));
  for (const ws of members) {
    const p = await waitFor(ws, (m) => m.t === 'presence' && m.members.length + m.overflow === 13);
    assert.equal(p.members.length, 12);
    assert.equal(p.overflow, 1);
    assert.ok(p.members.some((m) => m.id === ws.welcome.yourId), 'recipient always included');
  }
  for (let i = 14; i <= 24; i += 1) members.push(await join(S.port, { clientId: cid(100 + i), pasture: 'big' }));
  assert.equal(members.length, 24);
  const extra = await connect(S.port);
  sendJson(extra, { t: 'hello', protocolVersion: 1, clientId: cid(125), pasture: 'big', displayName: 'late' });
  const err = await waitFor(extra, (m) => m.t === 'error');
  assert.equal(err.code, 'pasture_full');
  assert.equal((await extra.closed).code, 4001);
  // an existing member reconnecting does not count as a 25th
  const again = await join(S.port, { clientId: cid(101), pasture: 'big' });
  assert.equal(again.welcome.yourId, cid(101));
  closeAll(members.concat([again]));
});

test('S1: chat before hello closes with 4004; hello deadline closes with 4004', async () => {
  const ws = await connect(S.port);
  sendJson(ws, { t: 'chat', text: 'too early' });
  assert.equal((await ws.closed).code, 4004);
  const T = await startServer({ helloTimeoutMs: 150 });
  try {
    const slow = await connect(T.port);
    const c = await slow.closed;
    assert.equal(c.code, 4004);
  } finally { await T.stop(); }
});

test('S1: malformed JSON, 5KB frame, binary frame each close 4004 and the server survives', async () => {
  const bad = await connect(S.port);
  bad.send('{not json');
  assert.equal((await bad.closed).code, 4004);
  const big = await connect(S.port);
  big.send(JSON.stringify({ t: 'hello', pad: 'x'.repeat(5000) }));
  assert.equal((await big.closed).code, 4004);
  const bin = await connect(S.port);
  bin.send(Buffer.from('{"t":"ping"}'), { binary: true });
  assert.equal((await bin.closed).code, 4004);
  const huge = await connect(S.port); // above the ws hard limit too
  huge.send('x'.repeat(70_000));
  assert.ok([4004, 1009, 1006].includes((await huge.closed).code));
  const ok = await join(S.port, { clientId: cid(61), pasture: 'alive' });
  assert.equal(ok.welcome.yourId, cid(61));
  closeAll([ok]);
});

test('S1: hello rejections: version mismatch -> error+4000, bad pasture -> pasture_invalid, bad clientId -> 4004', async () => {
  const v = await connect(S.port);
  sendJson(v, { t: 'hello', protocolVersion: 3, clientId: cid(1), pasture: 'commons' });
  assert.equal((await waitFor(v, (m) => m.t === 'error')).code, 'version');
  assert.equal((await v.closed).code, 4000);
  const p = await connect(S.port);
  sendJson(p, { t: 'hello', protocolVersion: 1, clientId: cid(1), pasture: 'Not Valid!' });
  assert.equal((await waitFor(p, (m) => m.t === 'error')).code, 'pasture_invalid');
  assert.equal((await p.closed).code, 4004);
  const c = await connect(S.port);
  sendJson(c, { t: 'hello', protocolVersion: 1, clientId: 'short', pasture: 'commons' });
  assert.equal((await waitFor(c, (m) => m.t === 'error')).code, 'malformed');
  assert.equal((await c.closed).code, 4004);
});

test('S1: pasture code lowercased, name and variant substituted, unknown message types ignored', async () => {
  const ws = await join(S.port, { clientId: cid(71), pasture: 'MiXeD-Case', displayName: '\u0000  ', variant: 'Not Valid' });
  assert.equal(ws.welcome.pasture, 'mixed-case');
  const p = await waitFor(ws, presenceWith(1));
  assert.deepEqual(p.members[0], { id: cid(71), name: 'cow', variant: 'brown' });
  sendJson(ws, { t: 'typing_v2', x: 1 });
  sendJson(ws, { t: 'ping' });
  assert.equal((await waitFor(ws, (m) => m.t === 'pong')).t, 'pong');
  assert.equal(ws.readyState, WebSocket.OPEN);
  closeAll([ws]);
});

test('S2: chat from one client arrives at all three including the sender', async () => {
  const a = await join(S.port, { clientId: cid(81), pasture: 'chat3', displayName: 'Ann' });
  const b = await join(S.port, { clientId: cid(82), pasture: 'chat3' });
  const c = await join(S.port, { clientId: cid(83), pasture: 'chat3' });
  sendJson(a, { t: 'chat', text: 'morning' });
  for (const ws of [a, b, c]) {
    const m = await waitFor(ws, (x) => x.t === 'chat');
    assert.equal(m.text, 'morning');
    assert.equal(m.fromId, cid(81));
    assert.equal(m.name, 'Ann');
    assert.ok(Math.abs(m.ts - Date.now()) < 5000);
  }
  closeAll([a, b, c]);
});

test('S2: 500-char and mid-emoji messages arrive truncated to 140 graphemes; junk neutralised end-to-end', async () => {
  const seg = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
  const a = await join(S.port, { clientId: cid(91), pasture: 'trunc' });
  const flag = '\u{1F1F5}\u{1F1F9}';
  const long = (flag + 'ab').repeat(130); // 520 code units
  sendJson(a, { t: 'chat', text: long });
  const m = await waitFor(a, (x) => x.t === 'chat');
  assert.equal([...seg.segment(m.text)].length, 140);
  assert.ok(long.startsWith(m.text));
  assert.ok(!m.text.endsWith('\u{1F1F5}'), 'no dangling half flag');
  sendJson(a, { t: 'chat', text: 'a\u0000b\u200Bc' + '\n'.repeat(200) + 'd\u202Ee' });
  assert.equal((await waitFor(a, (x) => x.t === 'chat')).text, 'abc de');
  sendJson(a, { t: 'chat', text: '   \u200B\u0007  ' }); // empty after normalisation: silently ignored
  await sleep(100);
  assert.equal(a.inbox.filter((x) => x.t === 'chat').length, 0);
  assert.equal(a.readyState, WebSocket.OPEN);
  closeAll([a]);
});

test('S2: 10 messages in 1s -> 3 relayed, rest dropped, connection survives', async () => {
  const a = await join(S.port, { clientId: cid(92), pasture: 'burst' });
  for (let i = 0; i < 10; i += 1) sendJson(a, { t: 'chat', text: `msg ${i}` });
  await sleep(300);
  const got = a.inbox.filter((x) => x.t === 'chat').map((x) => x.text);
  assert.deepEqual(got, ['msg 0', 'msg 1', 'msg 2']);
  assert.equal(a.readyState, WebSocket.OPEN);
  closeAll([a]);
});

test('S2: sustained flooding closes with 4002', async () => {
  const a = await join(S.port, { clientId: cid(93), pasture: 'flood' });
  for (let i = 0; i < 30; i += 1) sendJson(a, { t: 'chat', text: 'spam' });
  assert.equal((await waitFor(a, (x) => x.t === 'error')).code, 'rate_limited');
  assert.equal((await a.closed).code, 4002);
});

test('S2: log output never contains chat text', async () => {
  const phrase = 'zebra-pumpkin-' + Math.random().toString(36).slice(2);
  const a = await join(S.port, { clientId: cid(94), pasture: 'private' });
  sendJson(a, { t: 'chat', text: phrase });
  assert.equal((await waitFor(a, (x) => x.t === 'chat')).text, phrase);
  for (let i = 0; i < 30; i += 1) sendJson(a, { t: 'chat', text: phrase }); // dropped + abuse paths log too
  await a.closed;
  const all = S.logs.join('\n');
  assert.ok(!all.includes(phrase), 'phrase must not appear in logs');
  assert.ok(all.includes('"event":"chat"'), 'chat event is logged (id + bytes only)');
  for (const line of S.logs) JSON.parse(line); // every line is one JSON object
});

test('V2: welcome echoes protocolVersion 1 to a v1 client and 2 to a v2 client', async () => {
  const v1 = await join(S.port, { clientId: cid(301), pasture: 'ver', protocolVersion: 1 });
  const v2 = await join(S.port, { clientId: cid(302), pasture: 'ver', protocolVersion: 2 });
  assert.equal(v1.welcome.protocolVersion, 1);
  assert.equal(v2.welcome.protocolVersion, 2);
  assert.equal(v1.welcome.yourId, cid(301));
  const p = await waitFor(v2, presenceWith(2));
  assert.deepEqual(presenceIds(p), [cid(301), cid(302)], 'v1 and v2 members share one pasture');
  closeAll([v1, v2]);
});

test('V2: emote-only chat reaches v2 members with emote and no text; v1 member receives nothing', async () => {
  const a = await join(S.port, { clientId: cid(311), pasture: 'emote', displayName: 'Ann', protocolVersion: 2 });
  const b = await join(S.port, { clientId: cid(312), pasture: 'emote', protocolVersion: 2 });
  const old = await join(S.port, { clientId: cid(313), pasture: 'emote', protocolVersion: 1 });
  await waitFor(a, presenceWith(3));
  sendJson(a, { t: 'chat', emote: 'jump' });
  for (const ws of [a, b]) {
    const m = await waitFor(ws, (x) => x.t === 'chat');
    assert.equal(m.emote, 'jump');
    assert.equal(m.fromId, cid(311));
    assert.equal(m.name, 'Ann');
    assert.ok(!('text' in m), 'no text field');
    assert.ok(!('reaction' in m), 'no reaction field');
    assert.ok(Math.abs(m.ts - Date.now()) < 5000);
  }
  await sleep(150);
  assert.equal(old.inbox.filter((x) => x.t === 'chat').length, 0, 'v1 member gets no emote frame');
  assert.equal(old.readyState, WebSocket.OPEN);
  closeAll([a, b, old]);
});

test('V2: text+reaction reaches v1 as text only (v1 shape) and v2 with both', async () => {
  const heart = '\u2764\uFE0F';
  const a = await join(S.port, { clientId: cid(321), pasture: 'react', displayName: 'Ann', protocolVersion: 2 });
  const old = await join(S.port, { clientId: cid(322), pasture: 'react', protocolVersion: 1 });
  await waitFor(a, presenceWith(2));
  sendJson(a, { t: 'chat', text: 'hi', reaction: heart });
  const m2 = await waitFor(a, (x) => x.t === 'chat');
  assert.deepEqual(Object.keys(m2).sort(), ['fromId', 'name', 'reaction', 't', 'text', 'ts']);
  assert.equal(m2.text, 'hi');
  assert.equal(m2.reaction, heart);
  const m1 = await waitFor(old, (x) => x.t === 'chat');
  assert.deepEqual(Object.keys(m1).sort(), ['fromId', 'name', 't', 'text', 'ts']);
  assert.equal(m1.text, 'hi');
  assert.equal(m1.fromId, cid(321));
  // reaction-only: v2 gets it, v1 gets nothing
  sendJson(a, { t: 'chat', reaction: heart });
  assert.deepEqual(Object.keys(await waitFor(a, (x) => x.t === 'chat')).sort(), ['fromId', 'name', 'reaction', 't', 'ts']);
  await sleep(150);
  assert.equal(old.inbox.filter((x) => x.t === 'chat').length, 0);
  closeAll([a, old]);
});

test('V2: invalid or empty chat payload (unknown emote, non-emoji reaction) is relayed to nobody', async () => {
  const a = await join(S.port, { clientId: cid(331), pasture: 'junk', protocolVersion: 2 });
  const b = await join(S.port, { clientId: cid(332), pasture: 'junk', protocolVersion: 2 });
  await waitFor(a, presenceWith(2));
  sendJson(a, { t: 'chat', emote: 'dance' });
  sendJson(a, { t: 'chat', reaction: 'hi' });
  sendJson(a, { t: 'chat' });
  await sleep(150);
  assert.equal(a.inbox.filter((x) => x.t === 'chat').length, 0);
  assert.equal(b.inbox.filter((x) => x.t === 'chat').length, 0);
  assert.equal(a.readyState, WebSocket.OPEN);
  // a valid frame from a fresh sender still goes through (the three above spent a's tokens)
  sendJson(b, { t: 'chat', emote: 'moo' });
  assert.equal((await waitFor(a, (x) => x.t === 'chat')).emote, 'moo');
  closeAll([a, b]);
});

test('V2: log never contains the reaction emoji or text; chat line has bytes + emote/reaction flags', async () => {
  const rare = '\u{1FAB6}'; // feather: unlikely to appear in any other log line
  const phrase = 'giraffe-lantern-' + Math.random().toString(36).slice(2);
  const a = await join(S.port, { clientId: cid(341), pasture: 'privlog', protocolVersion: 2 });
  sendJson(a, { t: 'chat', text: phrase, emote: 'spin', reaction: rare });
  const m = await waitFor(a, (x) => x.t === 'chat');
  assert.equal(m.reaction, rare);
  const all = S.logs.join('\n');
  assert.ok(!all.includes(rare), 'emoji must not appear in logs');
  assert.ok(!all.includes(JSON.stringify(rare).slice(1, -1)), 'nor its escaped form');
  assert.ok(!all.includes(phrase), 'text must not appear in logs');
  const line = S.logs.map((l) => JSON.parse(l)).findLast((l) => l.event === 'chat' && l.fromId === cid(341));
  assert.equal(line.bytes, Buffer.byteLength(phrase));
  assert.equal(line.emote, true);
  assert.equal(line.reaction, true);
  assert.ok(!('text' in line));
  assert.ok(!all.includes('"spin"'), 'emote name is not logged either');
  closeAll([a]);
});

test('S3: banned clientId gets error+4003, clean clientId joins; ban takes effect on connected member', async () => {
  S.bans.ban(cid(201));
  const banned = await connect(S.port);
  sendJson(banned, { t: 'hello', protocolVersion: 1, clientId: cid(201), pasture: 'commons' });
  assert.equal((await waitFor(banned, (m) => m.t === 'error')).code, 'banned');
  assert.equal((await banned.closed).code, 4003);
  const clean = await join(S.port, { clientId: cid(202), pasture: 'commons' });
  assert.equal(clean.welcome.yourId, cid(202));
  S.bans.ban(cid(202)); // banned while connected -> evicted by the sweep
  assert.equal((await clean.closed).code, 4003);
  S.bans.unban(cid(201));
  const back = await join(S.port, { clientId: cid(201), pasture: 'commons' });
  assert.equal(back.welcome.yourId, cid(201));
  closeAll([back]);
});

test('S3: per-IP connection cap (X-Forwarded-For first hop, else socket address)', async () => {
  const T = await startServer({ ipCap: 2 });
  try {
    const h = (ip) => ({ 'x-forwarded-for': `${ip}, 127.0.0.1` });
    const a1 = await connect(T.port, h('203.0.113.9'));
    const a2 = await connect(T.port, h('203.0.113.9'));
    assert.equal(await connectExpectReject(T.port, h('203.0.113.9')), 429);
    const b1 = await connect(T.port, h('203.0.113.10')); // different IP unaffected
    a1.terminate();
    await waitFor(b1, () => false, 100).catch(() => {}); // give the server a tick to release the slot
    const a3 = await connect(T.port, h('203.0.113.9'));
    const s1 = await connect(T.port);
    const s2 = await connect(T.port);
    assert.equal(await connectExpectReject(T.port), 429); // socket address path
    assert.ok(T.logs.some((l) => l.includes('"event":"ip_cap"') && l.includes('203.0.113.0/24')), 'ip logged truncated');
    assert.ok(!T.logs.some((l) => l.includes('203.0.113.9')), 'full IP never logged');
    closeAll([a2, b1, a3, s1, s2]);
  } finally { await T.stop(); }
});

test('S3: /healthz reports counts on the same localhost server; other paths 404', async () => {
  const a = await join(S.port, { clientId: cid(211), pasture: 'health' });
  const get = (p) => new Promise((res, rej) => http.get({ host: '127.0.0.1', port: S.port, path: p }, (r) => {
    let body = ''; r.on('data', (d) => { body += d; }); r.on('end', () => res({ status: r.statusCode, body }));
  }).on('error', rej));
  const h = await get('/healthz');
  assert.equal(h.status, 200);
  const j = JSON.parse(h.body);
  assert.equal(j.ok, true);
  assert.ok(j.members >= 1 && j.pastures >= 1 && typeof j.uptimeSec === 'number');
  assert.equal((await get('/')).status, 404);
  const wrongPath = new WebSocket(`ws://127.0.0.1:${S.port}/other`);
  const status = await new Promise((r) => { wrongPath.on('unexpected-response', (_q, rs) => r(rs.statusCode)); wrongPath.on('error', () => r('err')); });
  assert.equal(status, 404);
  closeAll([a]);
});

test('S3: shutdown closes every client with 1001 and completes within 5s', async () => {
  const T = await startServer();
  const a = await join(T.port, { clientId: cid(221), pasture: 'bye' });
  const b = await join(T.port, { clientId: cid(222), pasture: 'bye' });
  const pre = await connect(T.port); // not yet joined
  const t0 = Date.now();
  await T.stop();
  assert.ok(Date.now() - t0 < 5000);
  for (const ws of [a, b, pre]) assert.equal((await ws.closed).code, 1001);
  await assert.rejects(connect(T.port));
});

test('S3: real process handles SIGTERM: clients get 1001, exit code 0 within 5s', { skip: process.platform === 'win32' && 'no SIGTERM handlers on Windows' }, async () => {
  const entry = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'src', 'server.js');
  const dataDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'data', 'test-' + process.pid);
  const child = spawn(process.execPath, [entry], { env: { ...process.env, COWPANION_PORT: '0', COWPANION_DATA: dataDir }, stdio: ['ignore', 'pipe', 'inherit'] });
  const port = await new Promise((resolve, reject) => {
    child.stdout.on('data', (d) => { for (const line of d.toString().split('\n')) if (line.includes('"listening"')) resolve(JSON.parse(line).port); });
    child.on('exit', (c) => reject(new Error('exited early ' + c)));
  });
  const ws = await join(port, { clientId: cid(231), pasture: 'sig' });
  const t0 = Date.now();
  child.kill('SIGTERM');
  const [closed, code] = await Promise.all([ws.closed, new Promise((r) => child.on('exit', r))]);
  assert.equal(closed.code, 1001);
  assert.equal(code, 0);
  assert.ok(Date.now() - t0 < 5000);
});
