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

/// <summary>A peer this user shares with / can browse (UUID + a friendly local name).</summary>
public sealed class SharePeer
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
}

// The persisted secret state: the seed phrase (identity) + the peer list. Serialized to JSON, then sealed
// into a single opaque blob (see SecureSharing.Save). Field names are short and generic on purpose.
internal sealed class ShareVaultData
{
    public string Seed { get; set; } = "";
    public List<SharePeer> Peers { get; set; } = new();
}

/// <summary>
/// Coordinator for secure peer-to-peer sharing. Owns the local cryptographic identity (derived from a
/// BIP39 seed) and peer list, both persisted as a single <b>opaque, label-less encrypted blob</b> with a
/// generic filename — at rest nothing hints that secure sharing exists (deniable storage). Also drives the
/// relay connection: registering, hosting an unlocked vault for approved peers, and browsing a peer's vault.
/// </summary>
public sealed class SecureSharing : IDisposable
{
    // Generic filename + label-less contents: indistinguishable from any other opaque cache blob at rest.
    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "store.dat");

    public PeerKeys Identity { get; private set; }
    public List<SharePeer> Peers => _data.Peers;
    public bool IsOnline => _relay is not null;

    private ShareVaultData _data;
    private RelayClient? _relay;
    private CancellationTokenSource? _hostCts;
    private string? _relayUrl;

    private SecureSharing(PeerKeys identity, ShareVaultData data)
    {
        Identity = identity;
        _data = data;
    }

    /// <summary>True once an identity has been created on this device. (The file is opaque; this only
    /// tells the app whether to show "unlock" vs "create" — it doesn't prove what the file is.)</summary>
    public static bool Exists() => File.Exists(StorePath);

    // ---- identity lifecycle ----

    /// <summary>Creates a brand-new identity from a freshly generated seed phrase. Returns the coordinator
    /// plus the seed phrase to show the user once for offline backup.</summary>
    public static (SecureSharing sharing, string seedPhrase) CreateNew(string passphrase)
    {
        var seed = PeerIdentity.GenerateSeedPhrase();
        var s = Recover(seed, passphrase);
        return (s, seed);
    }

    /// <summary>Creates/overwrites the identity from an existing seed phrase (wallet-style recovery).</summary>
    public static SecureSharing Recover(string seedPhrase, string passphrase)
    {
        if (!PeerIdentity.ValidateSeedPhrase(seedPhrase))
            throw new ArgumentException("That doesn't look like a valid recovery phrase.");
        var keys = PeerIdentity.FromSeedPhrase(seedPhrase);
        var data = new ShareVaultData { Seed = seedPhrase.Trim() };
        var s = new SecureSharing(keys, data);
        s.Save(passphrase);
        return s;
    }

    /// <summary>Opens the existing identity blob with the sharing passphrase. Throws on a wrong passphrase.</summary>
    public static SecureSharing Open(string passphrase)
    {
        var blob = File.ReadAllBytes(StorePath);
        if (blob.Length <= VaultCrypto.SaltSize) throw new InvalidDataException("Corrupt store.");
        var salt = blob[..VaultCrypto.SaltSize];
        var sealed_ = blob[VaultCrypto.SaltSize..];
        var kek = VaultCrypto.DeriveKey(passphrase, salt);
        try
        {
            var json = VaultCrypto.Decrypt(kek, sealed_); // throws CryptographicException on wrong passphrase
            var data = JsonSerializer.Deserialize<ShareVaultData>(json) ?? new ShareVaultData();
            var keys = PeerIdentity.FromSeedPhrase(data.Seed);
            return new SecureSharing(keys, data);
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    /// <summary>Re-seals identity + peers into the opaque blob (random salt each time).</summary>
    public void Save(string passphrase)
    {
        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.DeriveKey(passphrase, salt);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_data);
            var sealed_ = VaultCrypto.Encrypt(kek, json);
            var outp = new byte[salt.Length + sealed_.Length];
            Buffer.BlockCopy(salt, 0, outp, 0, salt.Length);
            Buffer.BlockCopy(sealed_, 0, outp, salt.Length, sealed_.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllBytes(StorePath, outp);
            CryptographicOperations.ZeroMemory(json);
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    // ---- peers ----

    public void AddPeer(string uuid, string name, string passphrase)
    {
        if (!Guid.TryParse(uuid?.Trim(), out var g)) throw new ArgumentException("Not a valid peer ID.");
        var id = g.ToString();
        if (id == Identity.Uuid.ToString()) throw new ArgumentException("That's your own ID.");
        var existing = _data.Peers.FirstOrDefault(p => p.Uuid == id);
        if (existing is not null) existing.Name = name?.Trim() ?? "";
        else _data.Peers.Add(new SharePeer { Uuid = id, Name = name?.Trim() ?? "" });
        Save(passphrase);
    }

    public void RemovePeer(string uuid, string passphrase)
    {
        _data.Peers.RemoveAll(p => p.Uuid == uuid);
        Save(passphrase);
    }

    private bool IsApprovedPeer(Guid uuid) => _data.Peers.Any(p => p.Uuid == uuid.ToString());

    // ---- relay connection + hosting ----

    /// <summary>Connects to the relay, registers this identity, and (if <paramref name="share"/> is given)
    /// begins accepting approved peers and serving that vault to them. Files never leave the host disk —
    /// only re-encrypted chunks stream out. Re-call to update the shared vault.</summary>
    public async Task GoOnlineAsync(string relayUrl, IShareSource? share, CancellationToken ct = default)
    {
        await GoOfflineAsync();
        _relayUrl = relayUrl;
        await RelayClient.RegisterAsync(relayUrl, Identity, ct);
        _relay = await RelayClient.ConnectAsync(relayUrl, Identity, ct);

        if (share is not null)
        {
            _hostCts = new CancellationTokenSource();
            _ = Task.Run(() => HostLoopAsync(share, _hostCts.Token));
        }
    }

    private async Task HostLoopAsync(IShareSource share, CancellationToken ct)
    {
        var relay = _relay!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var who = await relay.NewPeers.ReadAsync(ct);
                if (!IsApprovedPeer(who)) continue; // ignore unknown peers entirely
                _ = Task.Run(() => ServePeerAsync(relay, share, who, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch { /* relay closed */ }
    }

    private async Task ServePeerAsync(RelayClient relay, IShareSource share, Guid who, CancellationToken ct)
    {
        try
        {
            var peer = await RelayClient.LookupAsync(_relayUrl!, who, ct);
            if (peer is null) return;
            var hello = await relay.InboxFor(who).ReadAsync(ct);
            using var session = await SecureSession.AcceptAsync(relay, Identity, peer, hello, ct);
            await ShareProtocol.ServeAsync(relay, share, session, ct);
        }
        catch { /* drop this peer's session */ }
    }

    /// <summary>Opens a viewing session to a peer (we must be online; the peer must be online to answer).</summary>
    public async Task<SecureSession> ConnectToPeerAsync(Guid peerUuid, CancellationToken ct = default)
    {
        if (_relay is null || _relayUrl is null) throw new InvalidOperationException("Not online.");
        var peer = await RelayClient.LookupAsync(_relayUrl, peerUuid, ct)
                   ?? throw new InvalidOperationException("Peer not found on the relay.");
        return await SecureSession.InitiateAsync(_relay, Identity, peer, ct);
    }

    public RelayClient Relay => _relay ?? throw new InvalidOperationException("Not online.");

    /// <summary>Fetches this identity's access log from the relay.</summary>
    public Task<IReadOnlyList<RelayClient.AuditRecord>> QueryAuditAsync(string relayUrl, CancellationToken ct = default)
        => RelayClient.QueryAuditAsync(relayUrl, Identity, ct);

    public Task GoOfflineAsync()
    {
        try { _hostCts?.Cancel(); } catch { }
        _hostCts?.Dispose();
        _hostCts = null;
        _relay?.Dispose();
        _relay = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _hostCts?.Cancel(); } catch { }
        _hostCts?.Dispose();
        _relay?.Dispose();
        Identity.Wipe();
    }
}
