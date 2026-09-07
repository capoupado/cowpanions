// Structured single-line JSON logs on stdout (journald captures them).
// Never pass chat text in here. IPs must go through truncateIp() first.

const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 };

export function createLogger({ level = 'info', sink } = {}) {
  const threshold = LEVELS[level] ?? LEVELS.info;
  const write = sink ?? ((line) => process.stdout.write(line + '\n'));
  return function log(lvl, event, fields = {}) {
    if ((LEVELS[lvl] ?? LEVELS.info) < threshold) return;
    write(JSON.stringify({ ts: new Date().toISOString(), level: lvl, event, ...fields }));
  };
}

// IPv4 -> a.b.c.0/24, IPv6 -> first three hextets ::/48. Anything else -> "unknown".
export function truncateIp(ip) {
  if (typeof ip !== 'string' || ip === '') return 'unknown';
  const bare = ip.startsWith('::ffff:') ? ip.slice(7) : ip.split('%')[0];
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(bare)) {
    const p = bare.split('.');
    return `${p[0]}.${p[1]}.${p[2]}.0/24`;
  }
  if (bare.includes(':')) return `${expand6(bare).slice(0, 3).join(':')}::/48`;
  return 'unknown';
}

function expand6(addr) {
  const [l, r = ''] = addr.split('::');
  const left = l ? l.split(':') : [];
  const right = r ? r.split(':') : [];
  const fill = new Array(Math.max(0, 8 - left.length - right.length)).fill('0');
  return [...left, ...fill, ...right];
}
