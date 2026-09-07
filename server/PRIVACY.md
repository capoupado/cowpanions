# Cowpanion — Privacy

Cowpanion shows a few pixel cows on your screen. When online, a small relay server
(`cows.carlospoupado.com`, hosted in the EU) lets the cows of people who share a pasture code
see each other and exchange short messages. This page states exactly what that server keeps.

## What the server holds while you are connected (memory only)

- Your pseudonymous `clientId` (a random 32-character id generated once on your machine —
  not derived from your name, computer, or network).
- The display name and cow colour you chose, and the pasture code you typed.
- The time your connection was last active (for the 60-second idle timeout).
- Chat messages **pass through** the server to the other members of your pasture and are gone the
  moment they are relayed. Nothing is queued for offline members.

All of this disappears when you disconnect or the server restarts.

## What is written to disk

| Data | Where | Retention |
| --- | --- | --- |
| Chat text | **nowhere** — never written, at any log level | — |
| Ban list | `data/bans.sqlite` | until unbanned; stores **SHA-256(clientId)** only — no IP, no name |
| Server log (journald) | `join`/`leave`/`chat` events with pasture, clientId, display name, message **byte count**, and the client IP **truncated** to /24 (IPv4) or /48 (IPv6) | system journal default |
| Web-server access log (Apache) | request line, status, timestamp, **full client IP** | rotated daily, **deleted after 7 days** |

The Apache access log is the only place a full IP address exists, and it is the reason for the
7-day rule: chat text plus an IP address would be personal data under GDPR, so the server is
built so those two facts are never in the same place.

## What is never collected

No accounts, passwords, e-mail addresses, hostnames, usernames, OS versions, hardware ids,
usage statistics, or crash reports. The client sends only the fields in the wire protocol
(`docs/cowpanion-protocol.md`): `hello`, `chat`, `ping`, `bye`.

## Things to know

- A pasture code is a shared secret, not a login. Anyone who has the code can join and will see
  your display name and messages. Keep codes private.
- Moderation is a manual ban list. If a cow misbehaves, the pasture owner can ban its clientId;
  banning does not reveal who the person is.
- Resetting your clientId (delete it from the client config) makes you a new, unrelated cow.

Questions: carlos.a.poupado@criticalsoftware.com. This is a hobby project; this document is a
design commitment, not legal advice.
