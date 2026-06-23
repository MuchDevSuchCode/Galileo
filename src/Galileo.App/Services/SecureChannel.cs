using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>A peer's public identity as discovered from the relay (UUID + public keys).</summary>
public sealed class RemotePeer
{
    public required Guid Uuid { get; init; }
    public required byte[] SignPublic { get; init; }   // Ed25519 (32)
    public required byte[] AgreePublic { get; init; }  // X25519 (32)
}

/// <summary>
/// A live, authenticated connection to the Galileo relay. Registers/looks up peers over HTTP and relays
/// opaque end-to-end-encrypted envelopes over a WebSocket. The relay only ever sees ciphertext: handshake
/// and application frames are encrypted/signed between the two clients (see <see cref="SecureSession"/>).
/// Inbound envelopes are demultiplexed per source UUID so multiple peers can be served at once.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly PeerKeys _me;
    private readonly string _httpBase;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _inboxes = new();
    private readonly Channel<Guid> _newPeers = Channel.CreateUnbounded<Guid>();
    private Task? _recvLoop;

    private RelayClient(ClientWebSocket ws, PeerKeys me, string httpBase)
    {
        _ws = ws;
        _me = me;
        _httpBase = httpBase;
    }

    public Guid Uuid => _me.Uuid;

    /// <summary>Emits a peer UUID the first time an envelope arrives from it (host side: accept new viewers).</summary>
    public ChannelReader<Guid> NewPeers => _newPeers.Reader;

    /// <summary>Per-peer inbound queue of opaque payloads (created on demand).</summary>
    public ChannelReader<byte[]> InboxFor(Guid peer) => Inbox(peer).Reader;

    private Channel<byte[]> Inbox(Guid peer) =>
        _inboxes.GetOrAdd(peer, _ => Channel.CreateUnbounded<byte[]>());

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string HttpBaseFrom(string relayWsUrl)
    {
        var u = relayWsUrl.TrimEnd('/');
        if (u.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + u[6..];
        if (u.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + u[5..];
        return u; // already http(s)
    }

    private static string WsBaseFrom(string relayWsUrl)
    {
        var u = relayWsUrl.TrimEnd('/');
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + u[8..];
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + u[7..];
        return u;
    }

    // ---- HTTP: registration & discovery ----

    /// <summary>Registers (or refreshes) this identity's public keys with the relay.</summary>
    public static async Task RegisterAsync(string relayUrl, PeerKeys me, CancellationToken ct = default)
    {
        var http = HttpBaseFrom(relayUrl);
        var ts = UnixNow();
        var signPub = Convert.ToBase64String(me.SignPublic);
        var agreePub = Convert.ToBase64String(me.AgreePublic);
        var msg = $"register:{me.Uuid}:{signPub}:{agreePub}:{ts}";
        var sig = Convert.ToBase64String(PeerIdentity.Sign(me.SignPrivate, Encoding.UTF8.GetBytes(msg)));
        using var client = new HttpClient();
        var resp = await client.PostAsJsonAsync($"{http}/register",
            new { uuid = me.Uuid.ToString(), sign_pub = signPub, agree_pub = agreePub, ts, sig }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>One access record from the relay's audit log (object ids are opaque — never filenames).</summary>
    public sealed class AuditRecord
    {
        public required Guid Viewer { get; init; }
        public required string ObjectId { get; init; }
        public required string Action { get; init; }
        public required long Bytes { get; init; }
        public required DateTimeOffset Time { get; init; }
    }

    /// <summary>Fetches this identity's own access log from the relay (who viewed which opaque object, when).</summary>
    public static async Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(string relayUrl, PeerKeys me, CancellationToken ct = default)
    {
        var http = HttpBaseFrom(relayUrl);
        var ts = UnixNow();
        var sig = Convert.ToBase64String(PeerIdentity.Sign(me.SignPrivate, Encoding.UTF8.GetBytes($"audit-query:{me.Uuid}:{ts}")));
        using var client = new HttpClient();
        var resp = await client.PostAsJsonAsync($"{http}/audit/query", new { uuid = me.Uuid.ToString(), ts, sig }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<AuditRecord>();
        foreach (var r in doc.RootElement.GetProperty("records").EnumerateArray())
            list.Add(new AuditRecord
            {
                Viewer = Guid.TryParse(r.GetProperty("viewer_uuid").GetString(), out var v) ? v : Guid.Empty,
                ObjectId = r.GetProperty("object_id").GetString() ?? "",
                Action = r.GetProperty("action").GetString() ?? "",
                Bytes = r.GetProperty("bytes").GetInt64(),
                Time = DateTimeOffset.FromUnixTimeSeconds((long)r.GetProperty("ts").GetDouble()),
            });
        return list;
    }

    /// <summary>Looks up a peer's public keys by UUID. Returns null if the relay doesn't know it.</summary>
    public static async Task<RemotePeer?> LookupAsync(string relayUrl, Guid uuid, CancellationToken ct = default)
    {
        var http = HttpBaseFrom(relayUrl);
        using var client = new HttpClient();
        var resp = await client.GetAsync($"{http}/lookup/{uuid}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new RemotePeer
        {
            Uuid = uuid,
            SignPublic = Convert.FromBase64String(root.GetProperty("sign_pub").GetString()!),
            AgreePublic = Convert.FromBase64String(root.GetProperty("agree_pub").GetString()!),
        };
    }

    // ---- WebSocket: connect & relay ----

    /// <summary>Connects to the relay and authenticates this identity over the WebSocket.</summary>
    public static async Task<RelayClient> ConnectAsync(string relayUrl, PeerKeys me, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{WsBaseFrom(relayUrl)}/connect"), ct);

        var ts = UnixNow();
        var sig = Convert.ToBase64String(PeerIdentity.Sign(me.SignPrivate, Encoding.UTF8.GetBytes($"connect:{me.Uuid}:{ts}")));
        var auth = JsonSerializer.Serialize(new { uuid = me.Uuid.ToString(), ts, sig });
        await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);

        var ready = await ReceiveTextAsync(ws, ct) ?? throw new InvalidOperationException("Relay closed during authentication.");
        using (var doc = JsonDocument.Parse(ready))
        {
            if (doc.RootElement.GetProperty("type").GetString() != "ready")
                throw new InvalidOperationException("Relay rejected authentication.");
        }

        var client = new RelayClient(ws, me, HttpBaseFrom(relayUrl));
        client._recvLoop = Task.Run(client.ReceiveLoopAsync);
        return client;
    }

    /// <summary>Sends an opaque payload to a peer (the relay forwards it without inspecting it).</summary>
    public async Task SendAsync(Guid to, byte[] payload, CancellationToken ct = default)
    {
        var frame = JsonSerializer.Serialize(new { type = "relay", to = to.ToString(), payload = Convert.ToBase64String(payload) });
        await SendRawAsync(frame, ct);
    }

    /// <summary>Sends an audit record to the relay (host side: who accessed what, opaque object id).</summary>
    public async Task SendAuditAsync(Guid viewerUuid, string objectId, string action, long bytes, CancellationToken ct = default)
    {
        var frame = JsonSerializer.Serialize(new
        {
            type = "audit",
            record = new { viewer_uuid = viewerUuid.ToString(), object_id = objectId, action, bytes },
        });
        await SendRawAsync(frame, ct);
    }

    private async Task SendRawAsync(string frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(_ws, _cts.Token);
                if (text is null) break;
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;
                switch (t.GetString())
                {
                    case "relay":
                        if (Guid.TryParse(root.GetProperty("from").GetString(), out var from))
                        {
                            var payload = Convert.FromBase64String(root.GetProperty("payload").GetString() ?? "");
                            var fresh = !_inboxes.ContainsKey(from);
                            Inbox(from).Writer.TryWrite(payload);
                            if (fresh) _newPeers.Writer.TryWrite(from);
                        }
                        break;
                    // "error"/"pong" are advisory; ignore here (callers time out on missing replies).
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* socket closed/errored — inbox readers will observe cancellation on dispose */ }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// An end-to-end-encrypted, mutually authenticated session between two peers, layered over the relay.
/// The handshake mixes an ephemeral X25519 exchange (forward secrecy) with the static identity keys, and
/// each side signs its ephemeral key with Ed25519 (explicit mutual auth + transcript binding). Application
/// frames use AES-256-GCM with per-direction keys and a monotonic counter nonce (no nonce reuse, replay-safe).
/// </summary>
public sealed class SecureSession : IDisposable
{
    private const string HsContext = "galileo-hs-v1";
    private const string SessionInfo = "galileo-session-v1";

    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private long _sendCtr;
    private long _recvCtr;

    public Guid PeerUuid { get; }

    private SecureSession(Guid peer, byte[] sendKey, byte[] recvKey)
    {
        PeerUuid = peer;
        _sendKey = sendKey;
        _recvKey = recvKey;
    }

    /// <summary>Initiator side of the handshake (the viewer connecting to a host).</summary>
    public static async Task<SecureSession> InitiateAsync(RelayClient relay, PeerKeys me, RemotePeer them, CancellationToken ct = default)
    {
        // 1) send hello with our signed ephemeral public key
        var eph = GenerateEphemeral();
        try
        {
            var ephPub = Convert.ToBase64String(eph.AgreePublic);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = Convert.ToBase64String(PeerIdentity.Sign(me.SignPrivate,
                Encoding.UTF8.GetBytes($"{HsContext}|{me.Uuid}|{them.Uuid}|{ephPub}|{ts}")));
            await relay.SendAsync(them.Uuid,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = "hs1", eph = ephPub, ts, sig })), ct);

            // 2) receive hello-ack, verify it signs our ephemeral too (transcript binding)
            var reply = await ReadJsonAsync(relay.InboxFor(them.Uuid), ct);
            if (reply.GetProperty("t").GetString() != "hs2") throw new InvalidOperationException("Unexpected handshake reply.");
            var theirEphB64 = reply.GetProperty("eph").GetString()!;
            var theirTs = reply.GetProperty("ts").GetInt64();
            var theirSig = Convert.FromBase64String(reply.GetProperty("sig").GetString()!);
            var expect = Encoding.UTF8.GetBytes($"{HsContext}|{them.Uuid}|{me.Uuid}|{ephPub}|{theirEphB64}|{theirTs}");
            if (!PeerIdentity.Verify(them.SignPublic, expect, theirSig))
                throw new InvalidOperationException("Peer handshake signature invalid.");

            var theirEph = Convert.FromBase64String(theirEphB64);
            return Derive(me, them, eph.AgreePrivate, theirEph, weAreInitiator: true);
        }
        finally { eph.Wipe(); }
    }

    /// <summary>Responder side (the host accepting a viewer). <paramref name="firstHello"/> is the viewer's hs1 payload.</summary>
    public static async Task<SecureSession> AcceptAsync(RelayClient relay, PeerKeys me, RemotePeer them, byte[] firstHello, CancellationToken ct = default)
    {
        using var helloDoc = JsonDocument.Parse(firstHello);
        var hello = helloDoc.RootElement;
        if (hello.GetProperty("t").GetString() != "hs1") throw new InvalidOperationException("Expected handshake hello.");
        var theirEphB64 = hello.GetProperty("eph").GetString()!;
        var theirTs = hello.GetProperty("ts").GetInt64();
        var theirSig = Convert.FromBase64String(hello.GetProperty("sig").GetString()!);
        var expect = Encoding.UTF8.GetBytes($"{HsContext}|{them.Uuid}|{me.Uuid}|{theirEphB64}|{theirTs}");
        if (!PeerIdentity.Verify(them.SignPublic, expect, theirSig))
            throw new InvalidOperationException("Peer handshake signature invalid.");

        var eph = GenerateEphemeral();
        try
        {
            var ephPub = Convert.ToBase64String(eph.AgreePublic);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = Convert.ToBase64String(PeerIdentity.Sign(me.SignPrivate,
                Encoding.UTF8.GetBytes($"{HsContext}|{me.Uuid}|{them.Uuid}|{theirEphB64}|{ephPub}|{ts}")));
            await relay.SendAsync(them.Uuid,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = "hs2", eph = ephPub, ts, sig })), ct);

            var theirEph = Convert.FromBase64String(theirEphB64);
            return Derive(me, them, eph.AgreePrivate, theirEph, weAreInitiator: false);
        }
        finally { eph.Wipe(); }
    }

    // Mix ephemeral-ephemeral DH (forward secrecy) with static-static DH (binds to identities), then split
    // into per-direction keys keyed by a stable ordering of the two UUIDs.
    private static SecureSession Derive(PeerKeys me, RemotePeer them, byte[] myEphPriv, byte[] theirEphPub, bool weAreInitiator)
    {
        var ee = PeerIdentity.AgreeSharedSecret(myEphPriv, theirEphPub);
        var ss = PeerIdentity.AgreeSharedSecret(me.AgreePrivate, them.AgreePublic);
        try
        {
            var ikm = new byte[ee.Length + ss.Length];
            Buffer.BlockCopy(ee, 0, ikm, 0, ee.Length);
            Buffer.BlockCopy(ss, 0, ikm, ee.Length, ss.Length);

            // Stable A/B ordering so both ends agree which key is "a2b" vs "b2a".
            var aFirst = string.CompareOrdinal(me.Uuid.ToString(), them.Uuid.ToString()) < 0;
            var (a, b) = aFirst ? (me.Uuid, them.Uuid) : (them.Uuid, me.Uuid);
            var root = Hkdf(ikm, $"{SessionInfo}|{a}|{b}", 32);
            try
            {
                var keyAtoB = Hkdf(root, "a2b", 32);
                var keyBtoA = Hkdf(root, "b2a", 32);
                // I send with my-to-peer key; receive with peer-to-me key.
                var (sendKey, recvKey) = aFirst ? (keyAtoB, keyBtoA) : (keyBtoA, keyAtoB);
                return new SecureSession(them.Uuid, sendKey, recvKey);
            }
            finally { CryptographicOperations.ZeroMemory(root); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ee);
            CryptographicOperations.ZeroMemory(ss);
        }
    }

    // ---- transport framing: [12-byte nonce][16-byte tag][ciphertext], counter nonce per direction ----

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var nonce = NonceFor(_sendCtr++);
        var frame = new byte[12 + 16 + plaintext.Length];
        nonce.CopyTo(frame.AsSpan(0, 12));
        using var gcm = new AesGcm(_sendKey, 16);
        gcm.Encrypt(nonce, plaintext, frame.AsSpan(28), frame.AsSpan(12, 16));
        return frame;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 28) throw new CryptographicException("Frame too short.");
        var nonce = frame[..12];
        // Enforce the expected monotonic counter (reject replay/reorder on this reliable transport).
        var expected = NonceFor(_recvCtr);
        if (!CryptographicOperations.FixedTimeEquals(nonce, expected))
            throw new CryptographicException("Unexpected frame counter (possible replay).");
        _recvCtr++;
        var tag = frame.Slice(12, 16);
        var ct = frame[28..];
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(_recvKey, 16);
        gcm.Decrypt(nonce, ct, tag, plain);
        return plain;
    }

    /// <summary>Encrypt + send a plaintext message to the peer over the relay.</summary>
    public Task SendAsync(RelayClient relay, ReadOnlySpan<byte> plaintext, CancellationToken ct = default)
        => relay.SendAsync(PeerUuid, Encrypt(plaintext), ct);

    /// <summary>Receive + decrypt the next message from the peer.</summary>
    public async Task<byte[]> ReceiveAsync(RelayClient relay, CancellationToken ct = default)
        => Decrypt(await relay.InboxFor(PeerUuid).ReadAsync(ct));

    private static byte[] NonceFor(long counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    private static async Task<JsonElement> ReadJsonAsync(ChannelReader<byte[]> reader, CancellationToken ct)
    {
        var bytes = await reader.ReadAsync(ct);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static Ephemeral GenerateEphemeral()
    {
        var priv = VaultCrypto.RandomBytes(32);
        var pub = PeerIdentity.AgreePublicFromPrivate(priv);
        return new Ephemeral(pub, priv);
    }

    private static byte[] Hkdf(byte[] ikm, string info, int length)
        => PeerIdentity.Hkdf(ikm, info, length);

    private sealed class Ephemeral
    {
        public byte[] AgreePublic { get; }
        public byte[] AgreePrivate { get; }
        public Ephemeral(byte[] pub, byte[] priv) { AgreePublic = pub; AgreePrivate = priv; }
        public void Wipe() => CryptographicOperations.ZeroMemory(AgreePrivate);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_recvKey);
    }
}
