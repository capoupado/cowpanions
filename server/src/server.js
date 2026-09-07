// HTTP + WebSocket bootstrap, connection lifecycle, signal handling.
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocketServer, WebSocket } from 'ws';
import { createLogger, truncateIp } from './log.js';
import { openBans } from './bans.js';
import { Registry } from './registry.js';
import { ChatLimiter } from './ratelimit.js';
import {
  CLOSE, MAX_FRAME_BYTES, MAX_MEMBERS,
  parseFrame, validateHello, validateChatPayload,
  encodeWelcome, encodeError, encodePong,
} from './protocol.js';

// Production defaults match the protocol timing table exactly. Tests override.
export const DEFAULTS = {
  host: '127.0.0.1',
  port: 8787,
  dataDir: './data',
  logLevel: 'info',
  helloTimeoutMs: 10_000,
  evictionMs: 60_000,
  sweepIntervalMs: 10_000,
  presenceIntervalMs: 60_000,
  chatCapacity: 3,
  chatRefillMs: 3_000,
  abuseDrops: 20,
  abuseWindowMs: 60_000,
  ipCap: 4,
  shutdownGraceMs: 3_000,
};

export function createServer(overrides = {}) {
  const opt = { ...DEFAULTS, ...overrides };
  const log = createLogger({ level: opt.logLevel, sink: opt.logSink });
  const bans = opt.bans ?? openBans(path.join(opt.dataDir, 'bans.sqlite'));
  const registry = new Registry();
  const conns = new Set();      // every socket, joined or not
  const byClientId = new Map(); // clientId -> conn (global: one cow per person)
  const ipCounts = new Map();   // ip -> open connections
  const startedAt = Date.now();
  let shuttingDown = false;
  let sweepTimer = null;
  let presenceTimer = null;

  const httpServer = http.createServer(onRequest);
  // ws enforces this hard limit (closes 1009); we apply the protocol's 4096
  // ourselves below so the client sees 4004 as the protocol requires.
  const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME_BYTES * 4 });
  httpServer.on('upgrade', onUpgrade);

  function onRequest(req, res) {
    if (req.method === 'GET' && req.url === '/healthz') {
      const body = JSON.stringify({
        ok: true,
        uptimeSec: Math.floor((Date.now() - startedAt) / 1000),
        pastures: registry.pastureCount,
        members: registry.memberCount,
      });
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(body);
      return;
    }
    res.writeHead(404, { 'Content-Type': 'text/plain' });
    res.end('not found');
  }

  function clientIp(req) {
    // nginx fronts us on localhost, so trust the first hop of X-Forwarded-For.
    const xff = req.headers['x-forwarded-for'];
    if (typeof xff === 'string' && xff.trim() !== '') return xff.split(',')[0].trim();
    return req.socket.remoteAddress ?? 'unknown';
  }

  function rejectUpgrade(socket, status, text) {
    socket.write(`HTTP/1.1 ${status} ${text}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
    socket.destroy();
  }

  function onUpgrade(req, socket, head) {
    if (shuttingDown) return rejectUpgrade(socket, 503, 'Service Unavailable');
    if (req.url !== '/ws') return rejectUpgrade(socket, 404, 'Not Found');
    const ip = clientIp(req);
    if ((ipCounts.get(ip) ?? 0) >= opt.ipCap) {
      log('warn', 'ip_cap', { ip: truncateIp(ip), cap: opt.ipCap });
      return rejectUpgrade(socket, 429, 'Too Many Requests');
    }
    wss.handleUpgrade(req, socket, head, (ws) => onConnection(ws, ip));
  }

  function onConnection(ws, ip) {
    const conn = {
      ws, ip, id: null, name: null, variant: null, pasture: null, protocolVersion: null,
      lastSeen: Date.now(), helloTimer: null,
      limiter: new ChatLimiter({
        capacity: opt.chatCapacity, refillMs: opt.chatRefillMs,
        abuseDrops: opt.abuseDrops, abuseWindowMs: opt.abuseWindowMs,
      }),
    };
    conns.add(conn);
    ipCounts.set(ip, (ipCounts.get(ip) ?? 0) + 1);
    conn.helloTimer = setTimeout(() => closeWith(conn, CLOSE.MALFORMED, 'hello timeout'), opt.helloTimeoutMs);
    ws.on('message', (data, isBinary) => onMessage(conn, data, isBinary));
    ws.on('close', (code) => onClose(conn, code));
    ws.on('error', (err) => log('warn', 'socket_error', { ip: truncateIp(ip), error: err.message }));
    log('debug', 'connect', { ip: truncateIp(ip) });
  }

  function send(conn, frame) {
    if (conn.ws.readyState === WebSocket.OPEN) conn.ws.send(frame);
  }

  function closeWith(conn, code, reason) {
    const s = conn.ws.readyState;
    if (s === WebSocket.OPEN || s === WebSocket.CONNECTING) conn.ws.close(code, reason);
  }

  function onMessage(conn, data, isBinary) {
    if (data.length > MAX_FRAME_BYTES) return closeWith(conn, CLOSE.MALFORMED, 'frame too large');
    const parsed = parseFrame(data, isBinary);
    if (!parsed.ok) {
      log('info', 'malformed', { ip: truncateIp(conn.ip), reason: parsed.reason });
      return closeWith(conn, CLOSE.MALFORMED, parsed.reason);
    }
    conn.lastSeen = Date.now();
    const msg = parsed.msg;
    if (!conn.pasture) {
      if (msg.t !== 'hello') return closeWith(conn, CLOSE.MALFORMED, 'expected hello');
      return onHello(conn, msg);
    }
    switch (msg.t) {
      case 'ping': return send(conn, encodePong());
      case 'chat': return onChat(conn, msg);
      case 'bye': return closeWith(conn, CLOSE.NORMAL, 'bye');
      default: return undefined; // unknown types are ignored, not fatal
    }
  }

  function onHello(conn, msg) {
    clearTimeout(conn.helloTimer);
    const v = validateHello(msg);
    if (!v.ok) {
      send(conn, encodeError(v.error, v.message));
      log('info', 'hello_rejected', { ip: truncateIp(conn.ip), error: v.error });
      return closeWith(conn, v.error === 'version' ? CLOSE.VERSION : CLOSE.MALFORMED, v.error);
    }
    const { clientId, pasture: code, displayName, variant, protocolVersion } = v.hello;
    if (bans.isBanned(clientId)) {
      send(conn, encodeError('banned', 'client is banned'));
      log('info', 'banned_join', { ip: truncateIp(conn.ip), clientId });
      return closeWith(conn, CLOSE.BANNED, 'banned');
    }
    const existing = byClientId.get(clientId);
    if (existing) {
      leavePasture(existing);
      closeWith(existing, CLOSE.NORMAL, 'replaced by new connection');
    }
    const pasture = registry.getOrCreate(code);
    if (pasture.size >= MAX_MEMBERS) {
      registry.release(pasture);
      send(conn, encodeError('pasture_full', 'pasture is full'));
      log('info', 'pasture_full', { pasture: code, clientId });
      return closeWith(conn, CLOSE.FULL, 'pasture full');
    }
    Object.assign(conn, { id: clientId, name: displayName, variant, pasture, protocolVersion });
    pasture.add({ id: clientId, name: displayName, variant, protocolVersion, send: (f) => send(conn, f) });
    byClientId.set(clientId, conn);
    send(conn, encodeWelcome(clientId, code, Date.now(), protocolVersion));
    pasture.broadcastPresence();
    log('info', 'join', { pasture: code, clientId, name: displayName, ip: truncateIp(conn.ip), members: pasture.size });
  }

  function onChat(conn, msg) {
    const verdict = conn.limiter.allow();
    if (verdict === 'abuse') {
      send(conn, encodeError('rate_limited', 'too many messages'));
      log('warn', 'rate_abuse', { pasture: conn.pasture.code, clientId: conn.id });
      return closeWith(conn, CLOSE.RATE, 'rate limit abuse');
    }
    if (verdict === 'drop') return log('debug', 'chat_dropped', { clientId: conn.id });
    const payload = validateChatPayload(msg);
    if (payload.text === '' && payload.emote === '' && payload.reaction === '') return;
    conn.pasture.broadcastChat(conn.id, conn.name, Date.now(), payload);
    // Sizes and flags only: never the text or the emoji itself (no chat content in logs).
    log('info', 'chat', { pasture: conn.pasture.code, fromId: conn.id, bytes: Buffer.byteLength(payload.text),
      emote: payload.emote !== '', reaction: payload.reaction !== '' });
  }

  function leavePasture(conn) {
    const pasture = conn.pasture;
    if (!pasture) return;
    conn.pasture = null;
    pasture.remove(conn.id);
    if (byClientId.get(conn.id) === conn) byClientId.delete(conn.id);
    if (pasture.size === 0) registry.release(pasture);
    else pasture.broadcastPresence();
    log('info', 'leave', { pasture: pasture.code, clientId: conn.id, members: pasture.size });
  }

  function onClose(conn, code) {
    clearTimeout(conn.helloTimer);
    conns.delete(conn);
    const n = (ipCounts.get(conn.ip) ?? 1) - 1;
    if (n <= 0) ipCounts.delete(conn.ip); else ipCounts.set(conn.ip, n);
    leavePasture(conn);
    log('debug', 'disconnect', { ip: truncateIp(conn.ip), code });
  }

  function sweep() {
    const now = Date.now();
    for (const conn of conns) {
      if (now - conn.lastSeen > opt.evictionMs) {
        log('info', 'evict', { clientId: conn.id, pasture: conn.pasture?.code });
        closeWith(conn, CLOSE.NORMAL, 'idle');
      } else if (conn.id && bans.isBanned(conn.id)) {
        send(conn, encodeError('banned', 'client is banned'));
        closeWith(conn, CLOSE.BANNED, 'banned');
      }
    }
  }

  function refreshPresence() {
    for (const p of registry.all()) p.broadcastPresence();
  }

  function listen() {
    return new Promise((resolve, reject) => {
      httpServer.once('error', reject);
      httpServer.listen(opt.port, opt.host, () => {
        sweepTimer = setInterval(sweep, opt.sweepIntervalMs);
        presenceTimer = setInterval(refreshPresence, opt.presenceIntervalMs);
        const { port } = httpServer.address();
        log('info', 'listening', { host: opt.host, port });
        resolve({ host: opt.host, port });
      });
    });
  }

  // Broadcast 1001, drain briefly, then force-close whatever is left.
  async function shutdown(reason = 'shutdown') {
    if (shuttingDown) return;
    shuttingDown = true;
    clearInterval(sweepTimer);
    clearInterval(presenceTimer);
    log('info', 'shutdown', { reason, connections: conns.size });
    for (const conn of conns) closeWith(conn, CLOSE.GOING_AWAY, 'server going away');
    const deadline = Date.now() + opt.shutdownGraceMs;
    while (conns.size > 0 && Date.now() < deadline) await new Promise((r) => setTimeout(r, 25));
    for (const conn of conns) conn.ws.terminate();
    httpServer.closeAllConnections();
    await new Promise((r) => httpServer.close(() => r()));
    wss.close();
    if (!opt.bans) bans.close();
  }

  return {
    listen,
    shutdown,
    stats: () => ({ connections: conns.size, pastures: registry.pastureCount, members: registry.memberCount }),
  };
}

// --- entry point -------------------------------------------------------------

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const server = createServer({
    port: Number(process.env.COWPANION_PORT ?? DEFAULTS.port),
    dataDir: process.env.COWPANION_DATA ?? DEFAULTS.dataDir,
    logLevel: process.env.COWPANION_LOG_LEVEL ?? DEFAULTS.logLevel,
    ipCap: Number(process.env.COWPANION_IP_CAP ?? DEFAULTS.ipCap),
  });
  const log = createLogger();
  const stop = (signal) => server.shutdown(signal).then(() => process.exit(0), () => process.exit(1));
  process.on('SIGTERM', () => stop('SIGTERM'));
  process.on('SIGINT', () => stop('SIGINT'));
  process.on('uncaughtException', (err) => { log('error', 'uncaught', { error: err.message }); process.exit(1); });
  process.on('unhandledRejection', (err) => { log('error', 'unhandled_rejection', { error: String(err?.message ?? err) }); process.exit(1); });
  server.listen().catch((err) => { log('error', 'listen_failed', { error: err.message }); process.exit(1); });
}
