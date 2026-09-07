// Cowpanion wire protocol v2 (v1 hellos still accepted) — see docs/cowpanion-protocol.md.
// This is the ONLY module that knows message shapes. Everything downstream
// works with the trusted objects produced here.

export const PROTOCOL_VERSION = 2;
export const SUPPORTED_VERSIONS = [1, 2];
export const MAX_FRAME_BYTES = 4096;
export const MAX_MEMBERS = 24;
export const VISIBLE_CAP = 12;
export const CHAT_MAX_GRAPHEMES = 140;
export const NAME_MAX_GRAPHEMES = 16;
export const EMOTES = ['moo', 'jump', 'spin'];
export const REACTION_MAX_GRAPHEMES = 3;
export const DEFAULT_VARIANT = 'brown';
export const DEFAULT_NAME = 'cow';

export const CLOSE = {
  NORMAL: 1000, GOING_AWAY: 1001,
  VERSION: 4000, FULL: 4001, RATE: 4002, BANNED: 4003, MALFORMED: 4004,
};

const CLIENT_ID_RE = /^[0-9a-f]{32}$/i;
const PASTURE_RE = /^[a-z0-9-]{1,32}$/;
const VARIANT_RE = /^[a-z0-9_]{1,16}$/;

// Zero-width / invisible characters (beyond \p{Cf}) that must not survive.
const ZERO_WIDTH_RE = /[\u200B\u200C\u2060\uFEFF\u180E\u00AD\u034F\u061C\u115F\u1160\u17B4\u17B5\u3164\uFFA0]/gu;
// Bidi controls and Unicode tag characters (spoofing vectors).
const FORMAT_RE = /[\u202A-\u202E\u2066-\u2069\u{E0000}-\u{E007F}]/gu;
// ZWJ (U+200D) is kept only when it glues two pictographs together, so family
// and profession emoji survive while stray joiners are removed.
const STRAY_ZWJ_RE = /(?<![\p{Extended_Pictographic}\uFE0F\u{1F3FB}-\u{1F3FF}])\u200D|\u200D(?!\p{Extended_Pictographic})/gu;

// A reaction grapheme must contain at least one emoji-ish code point (pictograph,
// regional indicator for flags, emoji-presentation symbol, or the keycap enclosure).
const EMOJI_CLUSTER_RE = /\p{Extended_Pictographic}|\p{Regional_Indicator}|\p{Emoji_Presentation}|\u20E3/u;

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

// --- inbound -----------------------------------------------------------------

// Parse one frame. Returns { ok: true, msg } or { ok: false, reason }.
export function parseFrame(data, isBinary) {
  if (isBinary) return { ok: false, reason: 'binary frame' };
  let msg;
  try {
    msg = JSON.parse(data.toString('utf8'));
  } catch {
    return { ok: false, reason: 'invalid json' };
  }
  if (msg === null || typeof msg !== 'object' || Array.isArray(msg)) {
    return { ok: false, reason: 'not an object' };
  }
  if (typeof msg.t !== 'string') return { ok: false, reason: 'missing type tag' };
  return { ok: true, msg };
}

// Validate a hello. Returns { ok: true, hello } or { ok: false, error, message }
// where error is a protocol error code (version | malformed | pasture_invalid).
export function validateHello(msg) {
  if (!SUPPORTED_VERSIONS.includes(msg.protocolVersion)) {
    return { ok: false, error: 'version', message: 'protocol version mismatch' };
  }
  if (typeof msg.clientId !== 'string' || !CLIENT_ID_RE.test(msg.clientId)) {
    return { ok: false, error: 'malformed', message: 'clientId must be 32 hex chars' };
  }
  const pasture = typeof msg.pasture === 'string' ? msg.pasture.trim().toLowerCase() : '';
  if (!PASTURE_RE.test(pasture)) {
    return { ok: false, error: 'pasture_invalid', message: 'pasture code must match [a-z0-9-]{1,32}' };
  }
  const variant = typeof msg.variant === 'string' && VARIANT_RE.test(msg.variant)
    ? msg.variant : DEFAULT_VARIANT;
  return {
    ok: true,
    hello: {
      protocolVersion: msg.protocolVersion,
      clientId: msg.clientId.toLowerCase(),
      pasture,
      displayName: sanitiseName(msg.displayName),
      variant,
    },
  };
}

// Strip control + zero-width characters, collapse whitespace, trim.
export function normaliseText(input) {
  if (typeof input !== 'string') return '';
  // Zero-width first: \s matches U+FEFF, so it must go before whitespace collapse.
  return input
    .replace(ZERO_WIDTH_RE, '')
    .replace(FORMAT_RE, '')
    .replace(STRAY_ZWJ_RE, '')
    .replace(/\s+/gu, ' ')
    .replace(/\p{Cc}/gu, '')
    .replace(/\s+/gu, ' ')
    .trim();
}

// Truncate to at most `max` grapheme clusters without splitting any of them.
export function truncateGraphemes(text, max) {
  let count = 0;
  for (const { index } of segmenter.segment(text)) {
    if (count === max) return text.slice(0, index);
    count += 1;
  }
  return text;
}

export function normaliseChat(input) {
  return truncateGraphemes(normaliseText(input), CHAT_MAX_GRAPHEMES);
}

// Reactions: 1-3 grapheme clusters, every one of them emoji-like, else ''. Whitespace
// between emoji is dropped; more than 3 emoji are truncated to 3 (as chat is, not rejected).
export function normaliseReaction(input) {
  const text = normaliseText(input).replace(/\s+/gu, '');
  if (text === '') return '';
  const clusters = [...segmenter.segment(text)].map((c) => c.segment);
  if (!clusters.every((c) => EMOJI_CLUSTER_RE.test(c))) return '';
  return clusters.slice(0, REACTION_MAX_GRAPHEMES).join('');
}

// Trusted { text, emote, reaction } from an inbound chat; each '' when absent/invalid.
export function validateChatPayload(msg) {
  const emote = EMOTES.includes(msg.emote) ? msg.emote : '';
  return { text: normaliseChat(msg.text), emote, reaction: normaliseReaction(msg.reaction) };
}

export function sanitiseName(input) {
  const name = truncateGraphemes(normaliseText(input), NAME_MAX_GRAPHEMES);
  return name === '' ? DEFAULT_NAME : name;
}

// --- outbound ----------------------------------------------------------------

// protocolVersion echoes the version the client sent in its hello.
export function encodeWelcome(yourId, pasture, serverTime, protocolVersion = PROTOCOL_VERSION) {
  return JSON.stringify({
    t: 'welcome', protocolVersion, yourId, pasture,
    visibleCap: VISIBLE_CAP, serverTime,
  });
}

// members: array of { id, name, variant } already capped to VISIBLE_CAP.
export function encodePresence(members, overflow) {
  return JSON.stringify({
    t: 'presence',
    members: members.map((m) => ({ id: m.id, name: m.name, variant: m.variant })),
    overflow,
  });
}

// v2 recipients get only the non-empty fields; v1 recipients get the v1 shape
// when there is text, and null (send nothing) for emote/reaction-only frames.
export function encodeChat(fromId, name, ts, { text, emote, reaction }, recipientVersion = PROTOCOL_VERSION) {
  if (recipientVersion === 1) return text ? JSON.stringify({ t: 'chat', fromId, name, text, ts }) : null;
  const frame = { t: 'chat', fromId, name, ts };
  if (text) frame.text = text;
  if (emote) frame.emote = emote;
  if (reaction) frame.reaction = reaction;
  return JSON.stringify(frame);
}

export function encodeError(code, message) {
  return JSON.stringify({ t: 'error', code, message });
}

export function encodePong() {
  return JSON.stringify({ t: 'pong' });
}
