# Cowpanion — Privacy

Cowpanion is a desktop toy: a few pixel-art cows graze along the bottom of your screen. This file explains
exactly what it does with your data.

## What is stored on your machine

- `%APPDATA%\Cowpanion\config.json` — your settings, your chosen display name, your chosen cow colour, and a
  random `clientId` (32 hex characters). The `clientId` is generated once from the operating system's
  cryptographic random number generator. It is **not** derived from your hostname, user name, MAC address or
  anything else that identifies you or your computer.
- `%APPDATA%\Cowpanion\cowpanion.log` — a small diagnostic log (connection state, config warnings). Chat text is
  **never** written to this log or anywhere else on disk.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Cowpanion` — only if you turn on `startWithWindows`.

## What is sent over the network

Only if `multiplayerEnabled` is true (the default), and only to the single pasture server configured in
`serverUrl` (default `wss://cows.carlospoupado.com/ws`), over TLS:

- On connect: protocol version, your `clientId`, the pasture code, your display name, your cow colour.
- While connected: a `ping` every 20 seconds, and any chat message you deliberately type and send with Enter.
- On quit: a courtesy `bye`.

That is the complete list. No hostname, OS version, usage counters, crash reports or telemetry are ever sent.
There are no other network calls of any kind: no update checks, no analytics, no third-party services.

Set `multiplayerEnabled` to `false` (tray menu → Multiplayer) and the app makes **no** network connections at all.

## What other people see

Everyone in the same pasture sees your display name, your cow colour, and the chat messages you send. A pasture
code is a shared secret, not authentication — treat every pasture as public to anyone who has the code.

## What the server keeps

The server does not persist chat messages. Its access logs (which include IP addresses) rotate out after at most
7 days. See the server's own documentation for the full statement.

## Chat input

Cowpanion never intercepts your clicks or keystrokes. The overlay is click-through at all times except during
chat mode, which you start with `Ctrl+Alt+C`, which shows a visible indicator, and which ends automatically after
8 seconds of inactivity, on Enter, on Escape, or when you click anywhere else.
