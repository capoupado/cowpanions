// A pasture is a named room: a member map keyed by clientId, plus broadcast.
import { VISIBLE_CAP, encodePresence } from './protocol.js';

export class Pasture {
  constructor(code) {
    this.code = code;
    this.members = new Map(); // clientId -> { id, name, variant, send }
  }

  get size() { return this.members.size; }
  add(member) { this.members.set(member.id, member); }
  remove(id) { return this.members.delete(id); }

  broadcast(frame) {
    for (const m of this.members.values()) m.send(frame);
  }

  // Full list, recipient always included, at most VISIBLE_CAP entries.
  presenceFor(recipientId) {
    const all = [...this.members.values()];
    if (all.length <= VISIBLE_CAP) return encodePresence(all, 0);
    const me = this.members.get(recipientId);
    const visible = me ? [me] : [];
    for (const m of all) {
      if (visible.length >= VISIBLE_CAP) break;
      if (m !== me) visible.push(m);
    }
    return encodePresence(visible, all.length - visible.length);
  }

  broadcastPresence() {
    if (this.members.size <= VISIBLE_CAP) {
      this.broadcast(encodePresence([...this.members.values()], 0));
      return;
    }
    for (const m of this.members.values()) m.send(this.presenceFor(m.id));
  }
}
