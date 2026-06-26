using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>A mutual contact ("friend"). Linking is symmetric — once linked, either side can share or
/// revoke any vault with the other, and either can unfriend (which drops all shares both ways).</summary>
public sealed class Friend
{
    public string Uuid { get; set; } = "";
    public string Alias { get; set; } = "";      // the friend's display name, learned at link time
    public string Status { get; set; } = "";     // pending_out | pending_in | linked

    public bool IsLinked => Status == "linked";
}

// Persisted secret state, sealed into the opaque store.dat. Short, generic field names on purpose.
internal sealed class ShareVaultData
{
    public string Alias { get; set; } = "";                                   // this user's display name
    public string Seed { get; set; } = "";                                    // BIP39 identity seed
    public List<Friend> Friends { get; set; } = new();
    public Dictionary<string, List<string>> Grants { get; set; } = new();      // vaultId -> friends with (at least) read
    public Dictionary<string, List<string>> GrantsWrite { get; set; } = new(); // vaultId -> friends with write (a subset of Grants)
}

/// <summary>Per-friend, per-vault access level. Write implies read.</summary>
public enum ShareAccess { None = 0, Read = 1, Write = 2 }

/// <summary>
/// Coordinator for secure peer-to-peer sharing. Owns the local identity (from a BIP39 seed), a display
/// alias, a mutual friend list, and per-vault share grants — all persisted as one opaque, label-less
/// encrypted blob with a generic filename (deniable at rest). Drives the relay: registering, exchanging
/// friend requests via the relay's store-and-forward mailbox, hosting granted vaults to linked friends,
/// and browsing what friends share back.
/// </summary>
public sealed class SecureSharing : IDisposable
{
    private static string DefaultStorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "store.dat");

    private readonly string _storePath;

    public PeerKeys Identity { get; private set; }
    public string Alias => _data.Alias;
    public IReadOnlyList<Friend> Friends => _data.Friends;
    public bool IsOnline => _relay is not null;

    /// <summary>True while at least one friend has a live serve session open (is browsing our share). Used by
    /// the host to avoid auto-locking the vault out from under an active viewer.</summary>
    public bool HasActiveViewers => !_activeViewers.IsEmpty;

    /// <summary>Raised (on a background thread) when a new incoming friend request arrives — UI should
    /// marshal to its dispatcher and prompt the user to accept.</summary>
    public event Action<Friend>? FriendRequestReceived;
    /// <summary>Raised (on a background thread) whenever the friend list / grants change.</summary>
    public event Action? Changed;
    /// <summary>Raised (on a background thread) when a friend tells us they've stopped sharing (locked /
    /// revoked) — the viewer should tear down any live browse of that peer.</summary>
    public event Action<Guid>? ShareRevokedByOwner;
    /// <summary>Raised (on a background thread) when an owner we're browsing reports their shared vault
    /// changed — the viewer should re-list immediately instead of waiting for its periodic poll.</summary>
    public event Action<Guid>? VaultChangedByOwner;

    // Peers we're currently serving (have a live serve session with), so we can notify them on lock.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _activeViewers = new();

    private ShareVaultData _data;
    private string? _passphrase;
    private RelayClient? _relay;
    private string? _relayUrl;
    private CancellationTokenSource? _loopCts;
    private Func<Guid, IShareSource?>? _shareForPeer;
    private bool _intentionalOffline = true;

    private SecureSharing(PeerKeys identity, ShareVaultData data, string? passphrase, string storePath)
    {
        Identity = identity;
        _data = data;
        _passphrase = passphrase;
        _storePath = storePath;
    }

    public static bool Exists(string? storePath = null) => File.Exists(storePath ?? DefaultStorePath);

    public static void DeleteStore(string? storePath = null)
    {
        var p = storePath ?? DefaultStorePath;
        if (File.Exists(p)) VaultCrypto.OverwriteAndDelete(p);
    }

    // ---- identity lifecycle ----

    public static (SecureSharing sharing, string seedPhrase) CreateNew(string passphrase, string alias, string? storePath = null)
    {
        var seed = PeerIdentity.GenerateSeedPhrase();
        return (Recover(seed, passphrase, alias, storePath), seed);
    }

    public static SecureSharing Recover(string seedPhrase, string passphrase, string alias, string? storePath = null)
    {
        if (!PeerIdentity.ValidateSeedPhrase(seedPhrase))
            throw new ArgumentException("That doesn't look like a valid recovery phrase.");
        var keys = PeerIdentity.FromSeedPhrase(seedPhrase);
        var data = new ShareVaultData { Seed = seedPhrase.Trim(), Alias = alias.Trim() };
        var s = new SecureSharing(keys, data, passphrase, storePath ?? DefaultStorePath);
        s.Save();
        return s;
    }

    public static SecureSharing Open(string passphrase, string? storePath = null)
    {
        var path = storePath ?? DefaultStorePath;
        var blob = File.ReadAllBytes(path);
        if (blob.Length <= VaultCrypto.SaltSize) throw new InvalidDataException("Corrupt store.");
        var salt = blob[..VaultCrypto.SaltSize];
        var sealed_ = blob[VaultCrypto.SaltSize..];
        var kek = VaultCrypto.DeriveKey(passphrase, salt);
        try
        {
            var json = VaultCrypto.Decrypt(kek, sealed_); // throws on wrong passphrase
            var data = JsonSerializer.Deserialize<ShareVaultData>(json) ?? new ShareVaultData();
            var keys = PeerIdentity.FromSeedPhrase(data.Seed);
            return new SecureSharing(keys, data, passphrase, path);
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    public void Save()
    {
        if (_passphrase is null) throw new InvalidOperationException("No passphrase set.");
        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.DeriveKey(_passphrase, salt);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_data);
            var sealed_ = VaultCrypto.Encrypt(kek, json);
            var outp = new byte[salt.Length + sealed_.Length];
            Buffer.BlockCopy(salt, 0, outp, 0, salt.Length);
            Buffer.BlockCopy(sealed_, 0, outp, salt.Length, sealed_.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllBytes(_storePath, outp);
            CryptographicOperations.ZeroMemory(json);
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    // ---- friends (mutual contacts) ----

    public IEnumerable<Friend> LinkedFriends => _data.Friends.Where(f => f.IsLinked);
    public Friend? FindFriend(Guid uuid) => _data.Friends.FirstOrDefault(f => f.Uuid == uuid.ToString());
    private bool IsLinkedFriend(Guid uuid) => FindFriend(uuid)?.IsLinked == true;

    /// <summary>Sends a friend request to another user (by ID). They must accept before you're linked.</summary>
    public async Task SendFriendRequestAsync(Guid to, CancellationToken ct = default)
    {
        if (_relay is null || _relayUrl is null) throw new InvalidOperationException("Go online first.");
        if (to == Identity.Uuid) throw new ArgumentException("That's your own ID.");
        var existing = FindFriend(to);
        if (existing?.IsLinked == true) throw new InvalidOperationException("You're already linked with that user.");

        await SendMailAsync(to, "friend_req", await AliasPayloadAsync(to, ct), ct);
        if (existing is null) _data.Friends.Add(new Friend { Uuid = to.ToString(), Status = "pending_out" });
        else if (existing.Status != "linked") existing.Status = "pending_out";
        Save();
        Changed?.Invoke();
    }

    /// <summary>Accepts an incoming friend request, completing the mutual link.</summary>
    public async Task AcceptFriendAsync(Guid from, CancellationToken ct = default)
    {
        if (_relay is null) throw new InvalidOperationException("Go online first.");
        var f = FindFriend(from) ?? throw new InvalidOperationException("No such request.");
        await SendMailAsync(from, "friend_accept", await AliasPayloadAsync(from, ct), ct);
        f.Status = "linked";
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a friend and immediately drops every share to/from them (both directions).</summary>
    public async Task UnfriendAsync(Guid uuid, CancellationToken ct = default)
    {
        if (_relay is not null)
            try { await SendMailAsync(uuid, "unfriend", await SealEmptyAsync(uuid, ct), ct); } catch { /* still remove locally */ }
        RemoveFriendLocal(uuid);
        Save();
        Changed?.Invoke();
    }

    private void RemoveFriendLocal(Guid uuid)
    {
        _data.Friends.RemoveAll(f => f.Uuid == uuid.ToString());
        foreach (var list in _data.Grants.Values) list.RemoveAll(u => u == uuid.ToString());
        foreach (var list in _data.GrantsWrite.Values) list.RemoveAll(u => u == uuid.ToString());
    }

    // ---- per-vault share grants ----

    /// <summary>The access level a friend has to a vault. (Backward compatible: an old store with only the
    /// read grant list reads back as Read; no write grant ⇒ read-only, the safe default.)</summary>
    public ShareAccess AccessFor(string vaultId, Guid friend)
    {
        var id = friend.ToString();
        if (_data.GrantsWrite.TryGetValue(vaultId, out var w) && w.Contains(id)) return ShareAccess.Write;
        if (_data.Grants.TryGetValue(vaultId, out var r) && r.Contains(id)) return ShareAccess.Read;
        return ShareAccess.None;
    }

    public bool IsGranted(string vaultId, Guid friend) => AccessFor(vaultId, friend) != ShareAccess.None;
    public bool CanWrite(string vaultId, Guid friend) => AccessFor(vaultId, friend) == ShareAccess.Write;

    public IReadOnlyList<string> GrantedFriendIds(string vaultId) =>
        _data.Grants.TryGetValue(vaultId, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();

    public void SetGrant(string vaultId, Guid friend, ShareAccess level)
    {
        if (!IsLinkedFriend(friend)) throw new InvalidOperationException("Not a linked friend.");
        ApplyGrant(vaultId, friend, level);
    }

    /// <summary>Grant a vault directly to a peer by ID, with no friendship required — for granting your own
    /// other devices (e.g. the phone). The host serves any peer that holds a grant (see <see cref="HasAnyGrant"/>).</summary>
    public void SetGrantById(string vaultId, Guid peer, ShareAccess level)
    {
        if (peer == Identity.Uuid) throw new ArgumentException("That's this device's own ID.");
        ApplyGrant(vaultId, peer, level);
    }

    private void ApplyGrant(string vaultId, Guid peer, ShareAccess level)
    {
        var id = peer.ToString();
        var read = _data.Grants.TryGetValue(vaultId, out var rl) ? rl : (_data.Grants[vaultId] = new List<string>());
        var write = _data.GrantsWrite.TryGetValue(vaultId, out var wl) ? wl : (_data.GrantsWrite[vaultId] = new List<string>());
        read.Remove(id); write.Remove(id);
        if (level >= ShareAccess.Read) read.Add(id);
        if (level == ShareAccess.Write) write.Add(id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>True if this peer holds a grant to any vault (lets the host serve a directly-granted device
    /// even when it isn't a linked friend).</summary>
    public bool HasAnyGrant(Guid peer)
    {
        var id = peer.ToString();
        return _data.Grants.Values.Any(l => l.Contains(id));
    }

    /// <summary>UUIDs granted to a vault that are NOT linked friends (direct "grant by ID" entries) — so the
    /// share UI can list and revoke them.</summary>
    public IReadOnlyList<string> DirectGrantIds(string vaultId)
    {
        if (!_data.Grants.TryGetValue(vaultId, out var l)) return Array.Empty<string>();
        return l.Where(id => Guid.TryParse(id, out var g) && !IsLinkedFriend(g)).ToList();
    }

    // ---- relay connection ----

    /// <summary>Connects to the relay and registers. <paramref name="shareForPeer"/> resolves the live
    /// share source to serve a given connecting friend (apply grant gating there); return null to serve
    /// nothing. Starts the host loop (serves linked friends) and the mail loop (friend requests).</summary>
    public async Task GoOnlineAsync(string relayUrl, Func<Guid, IShareSource?> shareForPeer, CancellationToken ct = default)
    {
        await GoOfflineAsync();
        _relayUrl = relayUrl;
        _shareForPeer = shareForPeer;
        _intentionalOffline = false;
        await ConnectAndServeAsync(ct);
        _ = Task.Run(ReconnectMonitorAsync); // keep us online: reconnect if the connection drops
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        await RelayClient.RegisterAsync(_relayUrl!, Identity, ct);
        _relay = await RelayClient.ConnectAsync(_relayUrl!, Identity, ct);
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _ = Task.Run(() => HostLoopAsync(token));
        _ = Task.Run(() => MailLoopAsync(token));
    }

    // While we intend to be online, watch the connection; if it drops, reconnect (re-register, re-serve)
    // with backoff. This keeps a host reachable across network blips and idle closes.
    private async Task ReconnectMonitorAsync()
    {
        while (!_intentionalOffline)
        {
            var relay = _relay;
            if (relay is null) break;
            try { await relay.Completion; } catch { }
            if (_intentionalOffline) break;

            try { _loopCts?.Cancel(); } catch { }
            relay.Dispose();
            _relay = null;

            for (var delayMs = 2000; !_intentionalOffline; delayMs = Math.Min(delayMs * 2, 30000))
            {
                try { await ConnectAndServeAsync(CancellationToken.None); break; }
                catch { try { await Task.Delay(delayMs); } catch { } }
            }
        }
    }

    private async Task HostLoopAsync(CancellationToken ct)
    {
        var relay = _relay!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sess = await relay.NewSessions.ReadAsync(ct);
                // Serve linked friends, and peers granted directly by ID (your own other devices).
                if (!IsLinkedFriend(sess.Peer) && !HasAnyGrant(sess.Peer)) continue;
                _ = Task.Run(() => ServePeerAsync(relay, sess, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task ServePeerAsync(RelayClient relay, SessionId sess, CancellationToken ct)
    {
        _activeViewers[sess.Peer] = 1;
        try
        {
            var peer = await RelayClient.LookupAsync(_relayUrl!, sess.Peer, ct);
            if (peer is null) return;
            var hello = await relay.InboxFor(sess.Peer, sess.Sid).ReadAsync(ct);
            using var session = await SecureSession.AcceptAsync(relay, Identity, peer, sess.Sid, hello, ct);
            var src = _shareForPeer?.Invoke(sess.Peer) ?? EmptyShareSource.Instance; // grant-gated by the caller
            await ShareProtocol.ServeAsync(relay, src, session, ct);
        }
        catch { }
        finally { _activeViewers.TryRemove(sess.Peer, out _); }
    }

    /// <summary>Tell everyone we're currently serving that the share has ended (we locked / revoked) so
    /// their viewer tears the browse down. Sent over the mailbox channel (separate from serve sessions).</summary>
    public async Task NotifyShareEndedAsync(CancellationToken ct = default)
    {
        if (_relay is null) return;
        foreach (var peer in _activeViewers.Keys.ToList())
        {
            try { await SendMailAsync(peer, "share_revoked", await SealEmptyAsync(peer, ct), ct); } catch { }
        }
    }

    /// <summary>Push: tell everyone currently browsing our share that the vault changed, so their viewer
    /// re-lists at once instead of waiting for its periodic poll. Sent over the mailbox channel (separate
    /// from the serve session) and only to peers with a live browse session, so it can't accumulate.</summary>
    public async Task NotifyVaultChangedAsync(CancellationToken ct = default)
    {
        if (_relay is null) return;
        foreach (var peer in _activeViewers.Keys.ToList())
        {
            try { await SendMailAsync(peer, "vault_changed", await SealEmptyAsync(peer, ct), ct); } catch { }
        }
    }

    private async Task MailLoopAsync(CancellationToken ct)
    {
        var relay = _relay!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var m = await relay.Mail.ReadAsync(ct);
                try { await ProcessMailAsync(m, ct); } catch { /* ignore a bad message */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task ProcessMailAsync(MailItem m, CancellationToken ct)
    {
        var sender = await RelayClient.LookupAsync(_relayUrl!, m.From, ct);
        if (sender is null) return;
        if (!PeerIdentity.Verify(sender.SignPublic, MailSigned(m.Mtype, m.From, Identity.Uuid, m.Ts, m.Payload), m.Signature))
            return; // forged / tampered

        switch (m.Mtype)
        {
            case "friend_req":
            {
                var alias = ReadAlias(sender, m.Payload);
                var f = FindFriend(m.From);
                if (f is null)
                {
                    f = new Friend { Uuid = m.From.ToString(), Alias = alias, Status = "pending_in" };
                    _data.Friends.Add(f);
                    Save();
                    FriendRequestReceived?.Invoke(f);
                }
                else if (f.Status == "pending_out") // crossing requests → mutual link
                {
                    f.Status = "linked"; f.Alias = alias; Save(); Changed?.Invoke();
                }
                else { f.Alias = alias; Save(); Changed?.Invoke(); }
                break;
            }
            case "friend_accept":
            {
                var alias = ReadAlias(sender, m.Payload);
                var f = FindFriend(m.From) ?? new Friend { Uuid = m.From.ToString() };
                if (FindFriend(m.From) is null) _data.Friends.Add(f);
                f.Status = "linked"; f.Alias = alias;
                Save(); Changed?.Invoke();
                break;
            }
            case "unfriend":
                RemoveFriendLocal(m.From);
                Save(); Changed?.Invoke();
                break;
            case "share_revoked":
                ShareRevokedByOwner?.Invoke(m.From);
                break;
            case "vault_changed":
                VaultChangedByOwner?.Invoke(m.From);
                break;
        }
    }

    public async Task<SecureSession> ConnectToPeerAsync(Guid peerUuid, CancellationToken ct = default)
    {
        if (_relay is null || _relayUrl is null) throw new InvalidOperationException("Not online.");
        var peer = await RelayClient.LookupAsync(_relayUrl, peerUuid, ct)
                   ?? throw new InvalidOperationException("Peer not found on the relay.");
        return await SecureSession.InitiateAsync(_relay, Identity, peer, ct);
    }

    /// <summary>Whether a friend is currently connected to the relay (so we can browse them live).</summary>
    public async Task<bool> IsPeerOnlineAsync(Guid uuid, CancellationToken ct = default)
    {
        if (_relayUrl is null) return false;
        var p = await RelayClient.LookupAsync(_relayUrl, uuid, ct);
        return p is not null && await OnlineFlagAsync(uuid, ct);
    }

    private async Task<bool> OnlineFlagAsync(Guid uuid, CancellationToken ct)
    {
        // /lookup carries an "online" flag; LookupAsync drops it, so re-query lightweight here.
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var baseUrl = _relayUrl!.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/');
            var json = await http.GetStringAsync($"{baseUrl}/lookup/{uuid}", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("online", out var o) && o.GetBoolean();
        }
        catch { return false; }
    }

    public RelayClient Relay => _relay ?? throw new InvalidOperationException("Not online.");

    public Task<IReadOnlyList<RelayClient.AuditRecord>> QueryAuditAsync(string relayUrl, CancellationToken ct = default)
        => RelayClient.QueryAuditAsync(relayUrl, Identity, ct);

    public Task ClearAuditAsync(string relayUrl, CancellationToken ct = default)
        => RelayClient.ClearAuditAsync(relayUrl, Identity, ct);

    public Task GoOfflineAsync()
    {
        _intentionalOffline = true; // stop the reconnect monitor
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _loopCts = null;
        _relay?.Dispose();
        _relay = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _intentionalOffline = true;
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _relay?.Dispose();
        Identity.Wipe();
    }

    // ---- mail helpers ----

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task SendMailAsync(Guid to, string mtype, byte[] payload, CancellationToken ct)
    {
        var ts = UnixNow();
        var sig = PeerIdentity.Sign(Identity.SignPrivate, MailSigned(mtype, Identity.Uuid, to, ts, payload));
        await _relay!.SendMailAsync(to, mtype, payload, ts, sig, ct);
    }

    private static byte[] MailSigned(string mtype, Guid from, Guid to, long ts, byte[] payload)
    {
        var prefix = Encoding.UTF8.GetBytes($"mail-v1|{mtype}|{from}|{to}|{ts}|");
        var buf = new byte[prefix.Length + payload.Length];
        Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, buf, prefix.Length, payload.Length);
        return buf;
    }

    // Seal our alias to the recipient's key so the relay's mailbox can't read it.
    private async Task<byte[]> AliasPayloadAsync(Guid to, CancellationToken ct)
    {
        var peer = await RelayClient.LookupAsync(_relayUrl!, to, ct)
                   ?? throw new InvalidOperationException("Peer not found on the relay.");
        var json = JsonSerializer.SerializeToUtf8Bytes(new { alias = _data.Alias });
        return PeerIdentity.SealForPeer(Identity.AgreePrivate, peer.AgreePublic, json);
    }

    private async Task<byte[]> SealEmptyAsync(Guid to, CancellationToken ct)
    {
        var peer = await RelayClient.LookupAsync(_relayUrl!, to, ct)
                   ?? throw new InvalidOperationException("Peer not found on the relay.");
        return PeerIdentity.SealForPeer(Identity.AgreePrivate, peer.AgreePublic, Encoding.UTF8.GetBytes("{}"));
    }

    private string ReadAlias(RemotePeer sender, byte[] payload)
    {
        try
        {
            var plain = PeerIdentity.OpenFromPeer(Identity.AgreePrivate, sender.AgreePublic, payload);
            using var doc = JsonDocument.Parse(plain);
            return doc.RootElement.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
}
