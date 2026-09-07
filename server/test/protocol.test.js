import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  normaliseChat, normaliseText, sanitiseName, validateHello, parseFrame, truncateGraphemes,
} from '../src/protocol.js';
import { truncateIp } from '../src/log.js';
import { TokenBucket, ChatLimiter } from '../src/ratelimit.js';

const seg = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
const graphemes = (s) => [...seg.segment(s)].length;
const boundaries = (s) => new Set([...seg.segment(s)].map((x) => x.index).concat([s.length]));

test('S2: 500-char message truncates to 140 graphemes without splitting emoji', () => {
  const family = '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}\u200D\u{1F466}'; // ZWJ family, 11 code units
  const flag = '\u{1F1F5}\u{1F1F9}'; // regional indicator pair
  const combining = 'ȩ́'; // e + acute + cedilla
  let input = '';
  while (input.length < 500 || graphemes(input) < 160) input += family + flag + combining + 'x';
  assert.ok(input.length >= 500 && graphemes(input) > 140);
  const out = normaliseChat(input);
  assert.equal(graphemes(out), 140);
  assert.ok(input.startsWith(out), 'output is a prefix of the input');
  assert.ok(boundaries(input).has(out.length), 'cut lands on a grapheme boundary of the input');
  assert.ok(out.length > 140, 'emoji are multi-code-unit so 140 graphemes exceed 140 code units');
});

test('S2: a message ending mid-emoji sequence truncates to a valid string', () => {
  const family = '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}\u200D\u{1F466}';
  const input = 'a'.repeat(139) + family + family;
  const out = normaliseChat(input);
  assert.equal(out, 'a'.repeat(139) + family);
  assert.equal(graphemes(out), 140);
  const out2 = truncateGraphemes('a'.repeat(139) + '\u{1F1F5}\u{1F1F9}\u{1F1F5}\u{1F1F9}', 140);
  assert.equal(out2, 'a'.repeat(139) + '\u{1F1F5}\u{1F1F9}');
});

test('S2: control chars, zero-width chars, bidi controls and 200 newlines are neutralised', () => {
  assert.equal(normaliseChat('a\u0000b\u0007c\u001Bd'), 'abcd');
  assert.equal(normaliseChat('a\u200Bb\u200Cc\u2060d\uFEFFe\u00ADf'), 'abcdef');
  assert.equal(normaliseChat('a\u202Eb\u2066c'), 'abc');
  assert.equal(normaliseChat('hi' + '\n'.repeat(200) + 'there'), 'hi there');
  assert.equal(normaliseChat('\t\t  lots   of \r\n\r\n space  \t'), 'lots of space');
  assert.equal(normaliseChat('\u200D\u200D\u200D'), '');
  assert.equal(normaliseChat('x\u200Dy'), 'xy', 'stray ZWJ between letters removed');
  assert.equal(normaliseChat('   '), '');
  assert.equal(normaliseChat(42), '');
});

test('S2: legitimate ZWJ emoji sequences survive normalisation', () => {
  const family = '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}\u200D\u{1F466}';
  const scientist = '\u{1F469}\u{1F3FD}\u200D\u{1F52C}'; // woman + skin tone + ZWJ + microscope
  const rainbowFlag = '\u{1F3F3}️\u200D\u{1F308}';
  assert.equal(normaliseChat(family), family);
  assert.equal(normaliseChat(scientist), scientist);
  assert.equal(normaliseChat(rainbowFlag), rainbowFlag);
  assert.equal(graphemes(normaliseChat(family)), 1);
});

test('S1: displayName sanitised to 1-16 chars else "cow"', () => {
  assert.equal(sanitiseName('Carlos'), 'Carlos');
  assert.equal(sanitiseName('  Carlos   Poupado  '), 'Carlos Poupado');
  assert.equal(sanitiseName('\u0000\u200B  '), 'cow');
  assert.equal(sanitiseName(''), 'cow');
  assert.equal(sanitiseName(undefined), 'cow');
  assert.equal(sanitiseName('abcdefghijklmnopqrstuvwxyz'), 'abcdefghijklmnop');
  assert.equal(graphemes(sanitiseName('\u{1F1F5}\u{1F1F9}'.repeat(20))), 16);
});

test('S1: hello validation (version, clientId, pasture, variant)', () => {
  const base = { t: 'hello', protocolVersion: 1, clientId: 'A'.repeat(32), pasture: 'Commons', displayName: 'x', variant: 'white0' };
  const ok = validateHello(base);
  assert.equal(ok.ok, true);
  assert.deepEqual(ok.hello, { clientId: 'a'.repeat(32), pasture: 'commons', displayName: 'x', variant: 'white0' });
  assert.equal(validateHello({ ...base, protocolVersion: 2 }).error, 'version');
  assert.equal(validateHello({ ...base, protocolVersion: '1' }).error, 'version');
  assert.equal(validateHello({ ...base, clientId: 'zz' }).error, 'malformed');
  assert.equal(validateHello({ ...base, clientId: 12 }).error, 'malformed');
  assert.equal(validateHello({ ...base, pasture: 'has space' }).error, 'pasture_invalid');
  assert.equal(validateHello({ ...base, pasture: 'a'.repeat(33) }).error, 'pasture_invalid');
  assert.equal(validateHello({ ...base, pasture: '' }).error, 'pasture_invalid');
  assert.equal(validateHello({ ...base, pasture: undefined }).error, 'pasture_invalid');
  assert.equal(validateHello({ ...base, variant: 'Pink Spots!' }).hello.variant, 'brown');
  assert.equal(validateHello({ ...base, variant: 'a'.repeat(17) }).hello.variant, 'brown');
  assert.equal(validateHello({ ...base, variant: undefined }).hello.variant, 'brown');
  assert.equal(validateHello({ ...base, variant: 'white_pinkspots' }).hello.variant, 'white_pinkspots');
});

test('frame parsing rejects binary, bad JSON, non-objects and missing type tag', () => {
  assert.equal(parseFrame(Buffer.from('{"t":"ping"}'), true).ok, false);
  assert.equal(parseFrame(Buffer.from('{nope'), false).ok, false);
  assert.equal(parseFrame(Buffer.from('[1,2]'), false).ok, false);
  assert.equal(parseFrame(Buffer.from('null'), false).ok, false);
  assert.equal(parseFrame(Buffer.from('{"x":1}'), false).ok, false);
  assert.deepEqual(parseFrame(Buffer.from('{"t":"ping"}'), false), { ok: true, msg: { t: 'ping' } });
});

test('S3: IPs are truncated to /24 and /48', () => {
  assert.equal(truncateIp('203.0.113.77'), '203.0.113.0/24');
  assert.equal(truncateIp('::ffff:203.0.113.77'), '203.0.113.0/24');
  assert.equal(truncateIp('2001:db8:abcd:1234:5678:9abc:def0:1'), '2001:db8:abcd::/48');
  assert.equal(truncateIp('2001:db8::1'), '2001:db8:0::/48');
  assert.equal(truncateIp('::1'), '0:0:0::/48');
  assert.equal(truncateIp('fe80::1%eth0'), 'fe80:0:0::/48');
  assert.equal(truncateIp(''), 'unknown');
  assert.equal(truncateIp(undefined), 'unknown');
});

test('S2: token bucket capacity 3, refill 1 per 3s; abuse after 20 drops in 60s', () => {
  let t = 0;
  const now = () => t;
  const b = new TokenBucket({ capacity: 3, refillMs: 3000, now });
  assert.deepEqual([b.tryTake(), b.tryTake(), b.tryTake(), b.tryTake()], [true, true, true, false]);
  t = 2999; assert.equal(b.tryTake(), false);
  t = 3000; assert.equal(b.tryTake(), true);
  t = 3001; assert.equal(b.tryTake(), false);
  t = 100_000; assert.deepEqual([b.tryTake(), b.tryTake(), b.tryTake(), b.tryTake()], [true, true, true, false]);

  t = 0;
  const l = new ChatLimiter({ capacity: 3, refillMs: 3000, abuseDrops: 20, abuseWindowMs: 60_000, now });
  const verdicts = [];
  for (let i = 0; i < 23; i += 1) verdicts.push(l.allow());
  assert.deepEqual(verdicts.slice(0, 3), ['ok', 'ok', 'ok']);
  assert.equal(verdicts.filter((v) => v === 'drop').length, 19);
  assert.equal(verdicts[22], 'abuse');
});

test('normaliseText collapses all whitespace kinds to a single space', () => {
  assert.equal(normaliseText('a  b\u3000c'), 'a b c');
});
