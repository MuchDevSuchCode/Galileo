# Galileo relay server

A small rendezvous + end-to-end relay that lets two Galileo clients discover each other and exchange
**opaque, end-to-end-encrypted** data for secure vault sharing. The relay is deliberately dumb:

- It **stores no files** and **never sees plaintext**. Relayed payloads are encrypted between the two
  Galileo clients with a key the relay can't derive (authenticated X25519 ECDH → AES-256-GCM).
- File contents stay on the **host's** disk. The viewer streams chunks live over the relay; nothing is
  persisted server-side.

## What it does

| Endpoint | Purpose |
|---|---|
| `POST /register` | Register/refresh a peer's UUID ↔ public keys (Ed25519 sign + X25519 agree). Signed. |
| `GET /lookup/{uuid}` | Discover a peer's public keys + online status by UUID. |
| `WS /connect` | Authenticated WebSocket; relays opaque `{to, from, payload}` envelopes between online peers. |
| `GET /healthz` | Liveness + count of connected peers. |

All signed requests carry a fresh `ts` (±5 min) to blunt replay. Signatures are Ed25519 over a fixed
string format (see `relay.py` for the exact messages).

## Run

```bash
cd server
pip install -r requirements.txt
python relay.py          # listens on 0.0.0.0:8765
```

Environment overrides: `GALILEO_RELAY_PORT` (default 8765), `GALILEO_RELAY_DB` (default `relay.db`).

### Production (AWS Lightsail / Ubuntu)

`deploy.sh` provisions everything on a fresh instance: a Python venv, the relay as a hardened systemd
service (bound to loopback), and an nginx reverse proxy that terminates TLS and upgrades the WebSocket.
Open TCP 80 + 443 in the Lightsail firewall and point an A record at the instance first, then on the
instance (Ubuntu 24.04 LTS):

```bash
git clone https://github.com/MuchDevSuchCode/Galileo.git
cd Galileo/server
sudo DOMAIN=relay.example.com EMAIL=you@example.com ./deploy.sh
```

It's idempotent — to update later: `git pull && sudo DOMAIN=relay.example.com EMAIL=you@example.com ./deploy.sh`.
Omit `DOMAIN` for a quick plain-HTTP test (no TLS). Verify with `curl https://relay.example.com/healthz`.

Point Galileo at it via **Settings → Secure sharing → Relay URL** (e.g. `wss://your-host:8765`). For real
deployments put it behind TLS (a reverse proxy terminating HTTPS/WSS); the end-to-end encryption protects
payloads regardless, but TLS hides metadata (which UUIDs talk) from the network.

## Trust model

- The relay is **untrusted** for confidentiality: it can see *that* two UUIDs communicate and the size/timing
  of traffic, but not file names or contents.
- The relay is **trusted** for availability.
- Peers authenticate each other end-to-end by their Ed25519 keys + out-of-band fingerprint ("safety number").
