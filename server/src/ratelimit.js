// Token bucket (capacity 3, refill 1 per 3s in production) plus a sliding
// window counting drops; 20 drops in 60s is abuse.

export class TokenBucket {
  constructor({ capacity, refillMs, now = Date.now }) {
    this.capacity = capacity;
    this.refillMs = refillMs;
    this.now = now;
    this.tokens = capacity;
    this.last = now();
  }

  tryTake() {
    const t = this.now();
    const refilled = Math.floor((t - this.last) / this.refillMs);
    if (refilled > 0) {
      this.tokens = Math.min(this.capacity, this.tokens + refilled);
      this.last += refilled * this.refillMs;
    }
    if (this.tokens <= 0) return false;
    this.tokens -= 1;
    return true;
  }
}

export class ChatLimiter {
  constructor({ capacity, refillMs, abuseDrops, abuseWindowMs, now = Date.now }) {
    this.bucket = new TokenBucket({ capacity, refillMs, now });
    this.abuseDrops = abuseDrops;
    this.abuseWindowMs = abuseWindowMs;
    this.now = now;
    this.drops = [];
  }

  // Returns 'ok' (relay), 'drop' (silently discard) or 'abuse' (close 4002).
  allow() {
    if (this.bucket.tryTake()) return 'ok';
    const t = this.now();
    this.drops.push(t);
    while (this.drops.length && t - this.drops[0] > this.abuseWindowMs) this.drops.shift();
    return this.drops.length >= this.abuseDrops ? 'abuse' : 'drop';
  }
}
