"""
Galileo secure-sharing relay.

A rendezvous + dumb end-to-end relay for Galileo's peer-to-peer vault sharing. It:
  - registers a peer's UUID <-> public keys (Ed25519 signing + X25519 agreement),
  - lets a peer look another up by UUID (discovery),
  - relays opaque, end-to-end-encrypted envelopes between two online peers over WebSocket,
  - keeps an append-only audit log of file accesses (opaque object IDs only).

The relay never sees plaintext: relayed payloads are end-to-end encrypted between the two Galileo
clients, and audit records carry only opaque object IDs (a host-side hash of the file path), never
names or contents. The relay stores no files — it only forwards bytes between live sockets.

Run:  pip install -r requirements.txt  &&  python relay.py   (listens on 0.0.0.0:8765)
"""
from __future__ import annotations

import base64
import json
import os
import sqlite3
import time
from contextlib import closing

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from pydantic import BaseModel

DB_PATH = os.environ.get("GALILEO_RELAY_DB", "relay.db")
app = FastAPI(title="Galileo Relay")

# uuid -> live WebSocket of the peer that's currently connected
_online: dict[str, WebSocket] = {}


# ----------------------------- storage -----------------------------

def db() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    with closing(db()) as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS peers (
                uuid       TEXT PRIMARY KEY,
                sign_pub   TEXT NOT NULL,   -- base64 Ed25519 public
                agree_pub  TEXT NOT NULL,   -- base64 X25519 public
                updated    REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                host_uuid   TEXT NOT NULL,  -- the owner serving the files
                viewer_uuid TEXT NOT NULL,  -- the remote peer that accessed
                object_id   TEXT NOT NULL,  -- opaque: host-side hash of the path (never the name)
                action      TEXT NOT NULL,  -- list | open | stream
                bytes       INTEGER NOT NULL DEFAULT 0,
                ts          REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_host ON audit(host_uuid, ts);
            CREATE TABLE IF NOT EXISTS mailbox (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                to_uuid   TEXT NOT NULL,  -- recipient
                from_uuid TEXT NOT NULL,  -- sender
                mtype     TEXT NOT NULL,  -- friend_req | friend_accept | unfriend
                payload   TEXT NOT NULL,  -- base64 opaque ciphertext (relay can't read it)
                sig       TEXT NOT NULL,  -- base64 Ed25519 over from|to|mtype|ts|payload (verified by recipient)
                ts        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS mailbox_to ON mailbox(to_uuid, id);
            """
        )
        conn.commit()


# ----------------------------- crypto helpers -----------------------------

def b64d(s: str) -> bytes:
    return base64.b64decode(s)


def verify_sig(sign_pub_b64: str, message: bytes, sig_b64: str) -> bool:
    """Verify an Ed25519 signature; returns False on any error (never raises)."""
    try:
        Ed25519PublicKey.from_public_bytes(b64d(sign_pub_b64)).verify(b64d(sig_b64), message)
        return True
    except (InvalidSignature, ValueError, Exception):
        return False


def fresh(ts: int, skew: int = 300) -> bool:
    """Reject stale/forward-dated timestamps (replay window)."""
    return abs(time.time() - ts) <= skew


# ----------------------------- registration / discovery -----------------------------

class RegisterReq(BaseModel):
    uuid: str
    sign_pub: str   # base64
    agree_pub: str  # base64
    ts: int         # unix seconds (integer — so the signed-message string matches byte-for-byte across languages)
    sig: str        # base64 Ed25519 over "register:{uuid}:{sign_pub}:{agree_pub}:{ts}"


@app.post("/register")
def register(r: RegisterReq):
    if not fresh(r.ts):
        raise HTTPException(400, "stale timestamp")
    msg = f"register:{r.uuid}:{r.sign_pub}:{r.agree_pub}:{r.ts}".encode()
    if not verify_sig(r.sign_pub, msg, r.sig):
        raise HTTPException(401, "bad signature")
    with closing(db()) as conn:
        conn.execute(
            "INSERT INTO peers(uuid, sign_pub, agree_pub, updated) VALUES(?,?,?,?) "
            "ON CONFLICT(uuid) DO UPDATE SET sign_pub=excluded.sign_pub, "
            "agree_pub=excluded.agree_pub, updated=excluded.updated",
            (r.uuid, r.sign_pub, r.agree_pub, time.time()),
        )
        conn.commit()
    return {"ok": True}


@app.get("/lookup/{uuid}")
def lookup(uuid: str):
    with closing(db()) as conn:
        row = conn.execute("SELECT uuid, sign_pub, agree_pub FROM peers WHERE uuid=?", (uuid,)).fetchone()
    if not row:
        raise HTTPException(404, "unknown peer")
    return {"uuid": row["uuid"], "sign_pub": row["sign_pub"], "agree_pub": row["agree_pub"],
            "online": uuid in _online}


# ----------------------------- audit log -----------------------------

class AuditQuery(BaseModel):
    uuid: str
    ts: int
    sig: str  # Ed25519 over "audit-query:{uuid}:{ts}"


@app.post("/audit/query")
def audit_query(q: AuditQuery):
    """Returns the caller's own audit records (must prove ownership of the signing key)."""
    if not fresh(q.ts):
        raise HTTPException(400, "stale timestamp")
    with closing(db()) as conn:
        peer = conn.execute("SELECT sign_pub FROM peers WHERE uuid=?", (q.uuid,)).fetchone()
        if not peer or not verify_sig(peer["sign_pub"], f"audit-query:{q.uuid}:{q.ts}".encode(), q.sig):
            raise HTTPException(401, "bad signature")
        rows = conn.execute(
            "SELECT viewer_uuid, object_id, action, bytes, ts FROM audit "
            "WHERE host_uuid=? ORDER BY ts DESC LIMIT 50000",
            (q.uuid,),
        ).fetchall()
    return {"records": [dict(r) for r in rows]}


@app.post("/audit/clear")
def audit_clear(q: AuditQuery):
    """Deletes the caller's own audit records (signed over 'audit-clear:{uuid}:{ts}')."""
    if not fresh(q.ts):
        raise HTTPException(400, "stale timestamp")
    with closing(db()) as conn:
        peer = conn.execute("SELECT sign_pub FROM peers WHERE uuid=?", (q.uuid,)).fetchone()
        if not peer or not verify_sig(peer["sign_pub"], f"audit-clear:{q.uuid}:{q.ts}".encode(), q.sig):
            raise HTTPException(401, "bad signature")
        conn.execute("DELETE FROM audit WHERE host_uuid=?", (q.uuid,))
        conn.commit()
    return {"ok": True}


def record_audit(host_uuid: str, rec: dict) -> None:
    try:
        with closing(db()) as conn:
            conn.execute(
                "INSERT INTO audit(host_uuid, viewer_uuid, object_id, action, bytes, ts) VALUES(?,?,?,?,?,?)",
                (host_uuid, str(rec.get("viewer_uuid", "")), str(rec.get("object_id", "")),
                 str(rec.get("action", "")), int(rec.get("bytes", 0) or 0), time.time()),
            )
            conn.commit()
    except Exception:
        pass  # auditing must never break relaying


# ----------------------------- mailbox (store-and-forward) -----------------------------
# Small control messages (friend request / accept / unfriend) for peers who may be offline. The relay
# stores opaque ciphertext + routing/sig only; the alias and any details inside payload are end-to-end
# encrypted to the recipient. Delivered immediately if the recipient is online, else on their next connect.

async def deliver_mail(ws: WebSocket, row) -> None:
    await ws.send_text(json.dumps({
        "type": "mail", "from": row["from_uuid"], "mtype": row["mtype"],
        "payload": row["payload"], "sig": row["sig"], "ts": row["ts"],
    }))


async def flush_mailbox(ws: WebSocket, uuid: str) -> None:
    with closing(db()) as conn:
        rows = conn.execute(
            "SELECT id, from_uuid, mtype, payload, sig, ts FROM mailbox WHERE to_uuid=? ORDER BY id", (uuid,)
        ).fetchall()
    for r in rows:
        try:
            await deliver_mail(ws, r)
            with closing(db()) as conn:
                conn.execute("DELETE FROM mailbox WHERE id=?", (r["id"],))
                conn.commit()
        except Exception:
            break  # socket died mid-flush; leave the rest queued for next time


async def handle_mail(from_uuid: str, msg: dict) -> None:
    to = str(msg.get("to", ""))
    if not to:
        return
    record = {
        "from_uuid": from_uuid, "mtype": str(msg.get("mtype", "")),
        "payload": str(msg.get("payload", "")), "sig": str(msg.get("sig", "")),
        "ts": int(msg.get("ts", 0) or 0),
    }
    target = _online.get(to)
    if target is not None:
        try:
            await deliver_mail(target, {"id": 0, "to_uuid": to, **record})
            return
        except Exception:
            pass  # fall through to store if delivery failed
    with closing(db()) as conn:
        conn.execute(
            "INSERT INTO mailbox(to_uuid, from_uuid, mtype, payload, sig, ts) VALUES(?,?,?,?,?,?)",
            (to, record["from_uuid"], record["mtype"], record["payload"], record["sig"], record["ts"]),
        )
        conn.commit()


# ----------------------------- WebSocket relay -----------------------------
#
# Protocol (all JSON text frames):
#   client -> relay, first frame:  {"uuid","ts","sig"}  sig = Ed25519 over "connect:{uuid}:{ts}"
#   relay  -> client on success :  {"type":"ready"}
#   client -> relay  to forward :  {"type":"relay","to":<uuid>,"payload":<b64 opaque ciphertext>}
#   relay  -> target            :  {"type":"relay","from":<uuid>,"payload":<b64>}
#   client -> relay  audit      :  {"type":"audit","record":{viewer_uuid,object_id,action,bytes}}
#   relay  -> client  error     :  {"type":"error","reason":<str>}
#
# The relay never inspects "payload" — it is end-to-end encrypted between the two clients.

@app.websocket("/connect")
async def connect(ws: WebSocket):
    await ws.accept()
    uuid = None
    try:
        auth = json.loads(await ws.receive_text())
        uuid = str(auth.get("uuid", ""))
        ts = int(auth.get("ts", 0))
        with closing(db()) as conn:
            peer = conn.execute("SELECT sign_pub FROM peers WHERE uuid=?", (uuid,)).fetchone()
        if not peer or not fresh(ts) or not verify_sig(peer["sign_pub"], f"connect:{uuid}:{ts}".encode(), auth.get("sig", "")):
            await ws.send_text(json.dumps({"type": "error", "reason": "auth failed"}))
            await ws.close()
            return

        _online[uuid] = ws
        await ws.send_text(json.dumps({"type": "ready"}))
        await flush_mailbox(ws, uuid)

        while True:
            msg = json.loads(await ws.receive_text())
            kind = msg.get("type")
            if kind == "mail":
                await handle_mail(uuid, msg)
            elif kind == "relay":
                target = _online.get(str(msg.get("to", "")))
                if target is None:
                    await ws.send_text(json.dumps({"type": "error", "reason": "peer offline", "to": msg.get("to")}))
                    continue
                await target.send_text(json.dumps({
                    "type": "relay", "from": uuid,
                    "sid": msg.get("sid", ""), "payload": msg.get("payload", ""),
                }))
            elif kind == "audit":
                record_audit(uuid, msg.get("record", {}) or {})
            elif kind == "ping":
                await ws.send_text(json.dumps({"type": "pong"}))
    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        if uuid and _online.get(uuid) is ws:
            del _online[uuid]


@app.get("/healthz")
def healthz():
    return {"ok": True, "online": len(_online)}


init_db()

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=int(os.environ.get("GALILEO_RELAY_PORT", "8765")))
