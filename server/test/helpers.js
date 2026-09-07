// Test helpers: start a server on an ephemeral port with fast timers and drive
// it with real ws clients.
import { once } from 'node:events';
import { WebSocket } from 'ws';
import { createServer } from '../src/server.js';
import { openBans } from '../src/bans.js';

export const FAST = {
  helloTimeoutMs: 2_000,
  evictionMs: 5_000,
  sweepIntervalMs: 100,
  presenceIntervalMs: 60_000,
  shutdownGraceMs: 1_000,
  ipCap: 1_000, // every test client is 127.0.0.1; the ip-cap test lowers this itself
};

export async function startServer(overrides = {}) {
  const logs = [];
  const bans = overrides.bans ?? openBans(':memory:');
  const server = createServer({
    port: 0, logLevel: 'debug', logSink: (line) => logs.push(line), bans, ...FAST, ...overrides,
  });
  const { port } = await server.listen();
  return { server, port, logs, bans, stop: () => server.shutdown('test').then(() => bans.close()) };
}

export const cid = (n) => n.toString(16).padStart(32, '0');

// Connect a client. Resolves to a ws with an inbox and a `closed` promise.
export async function connect(port, headers = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`, { headers });
  ws.inbox = [];
  ws.waiters = [];
  ws.on('message', (data) => {
    const msg = JSON.parse(data.toString());
    ws.inbox.push(msg);
    for (const w of ws.waiters.splice(0)) w();
  });
  ws.closed = new Promise((resolve) => ws.on('close', (code, reason) => resolve({ code, reason: reason.toString() })));
  ws.on('error', () => {});
  await once(ws, 'open');
  return ws;
}

// Attempt a connection that we expect the server to refuse at the HTTP level.
export function connectExpectReject(port, headers = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`, { headers });
    ws.on('unexpected-response', (_req, res) => { resolve(res.statusCode); ws.terminate(); });
    ws.on('open', () => reject(new Error('expected rejection but connected')));
    ws.on('error', reject);
  });
}

// Wait for a message satisfying pred (searching the inbox first). Everything up to
// and including the match is consumed, so stale earlier messages cannot satisfy a
// later wait. Rejects on timeout.
export function waitFor(ws, pred, timeoutMs = 3_000) {
  return new Promise((resolve, reject) => {
    const check = () => {
      const i = ws.inbox.findIndex(pred);
      if (i >= 0) { clearTimeout(timer); return resolve(ws.inbox.splice(0, i + 1)[i]); }
      ws.waiters.push(check);
    };
    const timer = setTimeout(() => reject(new Error(`timeout waiting for message; inbox=${JSON.stringify(ws.inbox)}`)), timeoutMs);
    check();
  });
}

export const sendJson = (ws, obj) => ws.send(JSON.stringify(obj));

// Clients speak the current protocol unless fields.protocolVersion says otherwise
// (pass protocolVersion: 1 to act as a legacy client).
export const DEFAULT_PROTOCOL_VERSION = 2;

export async function hello(ws, fields = {}) {
  sendJson(ws, {
    t: 'hello', protocolVersion: DEFAULT_PROTOCOL_VERSION, clientId: cid(1), pasture: 'commons',
    displayName: 'cow', variant: 'brown', ...fields,
  });
  return waitFor(ws, (m) => m.t === 'welcome');
}

export async function join(port, fields = {}, headers = {}) {
  const ws = await connect(port, headers);
  ws.welcome = await hello(ws, fields);
  return ws;
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function closeAll(sockets) {
  for (const ws of sockets) if (ws.readyState === WebSocket.OPEN) ws.terminate();
}
