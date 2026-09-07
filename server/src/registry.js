// Pasture lookup. Pastures exist only while they have members.
import { Pasture } from './pasture.js';

export class Registry {
  constructor() { this.pastures = new Map(); }

  getOrCreate(code) {
    let p = this.pastures.get(code);
    if (!p) {
      p = new Pasture(code);
      this.pastures.set(code, p);
    }
    return p;
  }

  // Drop a pasture once it is empty.
  release(pasture) {
    if (pasture.size === 0) this.pastures.delete(pasture.code);
  }

  all() { return [...this.pastures.values()]; }
  get pastureCount() { return this.pastures.size; }
  get memberCount() {
    let n = 0;
    for (const p of this.pastures.values()) n += p.size;
    return n;
  }
}
