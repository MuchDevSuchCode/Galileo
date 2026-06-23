"""Smoke test for the relay: two mock peers register, discover, relay an opaque payload, audit.

Run the relay first (python relay.py), then: .venv\\Scripts\\python.exe test_relay.py
Exits non-zero on any failed assertion.
"""
import asyncio
import base64
import json
import time

import httpx
import websockets
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives import serialization

BASE = "http://127.0.0.1:8765"
WS = "ws://127.0.0.1:8765/connect"


def b64(b: bytes) -> str:
    return base64.b64encode(b).decode()


def raw_pub(k) -> bytes:
    return k.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


class Peer:
    def __init__(self, uuid):
        self.uuid = uuid
        self.sign = Ed25519PrivateKey.generate()
        self.agree = X25519PrivateKey.generate()
        self.sign_pub = b64(raw_pub(self.sign))
        self.agree_pub = b64(raw_pub(self.agree))

    def sig(self, msg: str) -> str:
        return b64(self.sign.sign(msg.encode()))


async def register(client, p: Peer):
    ts = int(time.time())
    msg = f"register:{p.uuid}:{p.sign_pub}:{p.agree_pub}:{ts}"
    r = await client.post(f"{BASE}/register", json={
        "uuid": p.uuid, "sign_pub": p.sign_pub, "agree_pub": p.agree_pub, "ts": ts, "sig": p.sig(msg)})
    assert r.status_code == 200, f"register failed: {r.status_code} {r.text}"


async def connect_ws(p: Peer):
    ws = await websockets.connect(WS)
    ts = int(time.time())
    await ws.send(json.dumps({"uuid": p.uuid, "ts": ts, "sig": p.sig(f"connect:{p.uuid}:{ts}")}))
    ready = json.loads(await ws.recv())
    assert ready.get("type") == "ready", f"auth failed: {ready}"
    return ws


async def main():
    host = Peer("11111111-1111-5111-8111-111111111111")
    viewer = Peer("22222222-2222-5222-8222-222222222222")

    async with httpx.AsyncClient() as client:
        await register(client, host)
        await register(client, viewer)
        print("OK register x2")

        r = await client.get(f"{BASE}/lookup/{host.uuid}")
        assert r.status_code == 200 and r.json()["sign_pub"] == host.sign_pub, "lookup mismatch"
        print("OK lookup")

        r = await client.get(f"{BASE}/lookup/does-not-exist")
        assert r.status_code == 404, "expected 404 for unknown peer"
        print("OK unknown -> 404")

        # bad signature is rejected
        ts = int(time.time())
        bad = await client.post(f"{BASE}/register", json={
            "uuid": host.uuid, "sign_pub": host.sign_pub, "agree_pub": host.agree_pub,
            "ts": ts, "sig": b64(b"\x00" * 64)})
        assert bad.status_code == 401, f"bad sig should be 401, got {bad.status_code}"
        print("OK bad signature -> 401")

        # relay an opaque payload viewer -> host
        host_ws = await connect_ws(host)
        viewer_ws = await connect_ws(viewer)
        payload = b64(b"opaque-ciphertext-the-relay-cannot-read")
        await viewer_ws.send(json.dumps({"type": "relay", "to": host.uuid, "payload": payload}))
        got = json.loads(await asyncio.wait_for(host_ws.recv(), timeout=5))
        assert got["type"] == "relay" and got["from"] == viewer.uuid and got["payload"] == payload, f"relay mismatch: {got}"
        print("OK relay opaque payload")

        # relay to offline peer -> error
        await viewer_ws.send(json.dumps({"type": "relay", "to": "00000000-0000-5000-8000-000000000000", "payload": payload}))
        err = json.loads(await asyncio.wait_for(viewer_ws.recv(), timeout=5))
        assert err["type"] == "error" and err["reason"] == "peer offline", f"expected offline error: {err}"
        print("OK relay to offline -> error")

        # host records an audit event
        await host_ws.send(json.dumps({"type": "audit", "record": {
            "viewer_uuid": viewer.uuid, "object_id": "abc123opaque", "action": "open", "bytes": 4096}}))
        await asyncio.sleep(0.3)

        ts = int(time.time())
        r = await client.post(f"{BASE}/audit/query", json={
            "uuid": host.uuid, "ts": ts, "sig": host.sig(f"audit-query:{host.uuid}:{ts}")})
        assert r.status_code == 200, f"audit query failed: {r.text}"
        recs = r.json()["records"]
        assert any(x["object_id"] == "abc123opaque" and x["viewer_uuid"] == viewer.uuid for x in recs), f"audit not found: {recs}"
        print("OK audit recorded + queried")

        # audit query with wrong signer is rejected
        ts = int(time.time())
        r = await client.post(f"{BASE}/audit/query", json={
            "uuid": host.uuid, "ts": ts, "sig": viewer.sig(f"audit-query:{host.uuid}:{ts}")})
        assert r.status_code == 401, f"cross-signed audit should be 401, got {r.status_code}"
        print("OK audit cross-signature -> 401")

        await host_ws.close()
        await viewer_ws.close()

    print("\nALL RELAY TESTS PASSED")


if __name__ == "__main__":
    asyncio.run(main())
