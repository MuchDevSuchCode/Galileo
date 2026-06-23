using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>What the share host needs from a vault: the entries to expose and a read stream per entry.
/// <see cref="Vault"/> implements this; the protocol depends only on the seam (not on vault internals).</summary>
public interface IShareSource
{
    /// <summary>Display name of the shared vault (shown to the viewer); empty when nothing is shared.</summary>
    string ShareName { get; }
    IReadOnlyList<VaultEntry> ShareEntries();
    Stream OpenSharedEntry(string blobId);
}

/// <summary>Serves nothing — used when a friend has no current grant or the owner has no vault open.</summary>
public sealed class EmptyShareSource : IShareSource
{
    public static readonly EmptyShareSource Instance = new();
    public string ShareName => "";
    public IReadOnlyList<VaultEntry> ShareEntries() => Array.Empty<VaultEntry>();
    public Stream OpenSharedEntry(string blobId) => throw new FileNotFoundException("Not shared.");
}

/// <summary>Wraps a source so it only exposes anything while <paramref name="isGranted"/> returns true —
/// lets share/revoke take effect live, per request, without tearing down the connection.</summary>
public sealed class GrantGatedSource : IShareSource
{
    private readonly Func<bool> _isGranted;
    private readonly IShareSource _inner;
    public GrantGatedSource(Func<bool> isGranted, IShareSource inner) { _isGranted = isGranted; _inner = inner; }
    public string ShareName => _isGranted() ? _inner.ShareName : "";
    public IReadOnlyList<VaultEntry> ShareEntries() => _isGranted() ? _inner.ShareEntries() : Array.Empty<VaultEntry>();
    public Stream OpenSharedEntry(string blobId) =>
        _isGranted() ? _inner.OpenSharedEntry(blobId) : throw new FileNotFoundException("Not shared.");
}

/// <summary>A host's shared vault as the viewer sees it: the vault name plus its entries.</summary>
public sealed class SharedListing
{
    public required string VaultName { get; init; }
    public required IReadOnlyList<SharedItem> Items { get; init; }
}

/// <summary>One entry in a remote peer's shared vault, as seen by the viewer.</summary>
public sealed class SharedItem
{
    public required string Id { get; init; }       // opaque BlobId — also the audit object id
    public required string Name { get; init; }     // relative path within the vault (E2E-encrypted on the wire)
    public required long Size { get; init; }
    public required long ModifiedUtcTicks { get; init; }
}

/// <summary>
/// The application-level share protocol layered over a <see cref="SecureSession"/>. The host serves a list
/// of its unlocked vault's entries and streams their bytes chunk-by-chunk; the viewer lists and fetches.
/// Every request/response is end-to-end encrypted, so the relay only ever sees ciphertext. File contents
/// stay on the host's disk: the host reads from its ACL-restricted working folder and streams, and the
/// viewer holds plaintext only transiently (to view) unless it explicitly saves.
///
/// Messages are JSON (then AES-GCM framed by the session). Strictly request → response, one outstanding
/// at a time, so ordering on the reliable relay channel is preserved.
/// </summary>
public static class ShareProtocol
{
    public const int ChunkSize = 256 * 1024; // raw plaintext bytes per chunk

    private static byte[] Utf8(object o) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o));

    // ---------------- Host (serves a vault to one viewer over one session) ----------------

    /// <summary>Serves requests from one viewer until the session ends or <paramref name="ct"/> is cancelled.
    /// Emits opaque audit events (object = BlobId) to the relay as the viewer lists/opens files.</summary>
    public static async Task ServeAsync(RelayClient relay, IShareSource vault, SecureSession session, CancellationToken ct)
    {
        // Track which files this viewer currently has open in their viewer, so we can audit a "close" for
        // anything still open if the session ends (disconnect/crash) — no permanently-stale "still open".
        var openIds = new HashSet<string>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] reqBytes;
                try { reqBytes = await session.ReceiveAsync(relay, ct); }
                catch { break; } // session closed / cancelled / framing error → stop serving this peer

                try
                {
                    using var doc = JsonDocument.Parse(reqBytes);
                    var root = doc.RootElement;
                    switch (root.GetProperty("op").GetString())
                    {
                        case "list":
                            await HandleListAsync(relay, vault, session, ct);
                            break;
                        case "open":
                            // Pure chunk transfer (for thumbnails / fetching) — not audited; only an
                            // actual viewer "view" counts as access.
                            await HandleOpenAsync(relay, vault, session, root, ct);
                            break;
                        case "view":
                        {
                            // Viewer opened a file in its viewer — the access we log.
                            var id = root.GetProperty("id").GetString() ?? "";
                            if (openIds.Add(id))
                                await relay.SendAuditAsync(session.PeerUuid, id, "open", SizeOf(vault, id), ct);
                            break;
                        }
                        case "close":
                        {
                            var id = root.GetProperty("id").GetString() ?? "";
                            if (openIds.Remove(id))
                                await relay.SendAuditAsync(session.PeerUuid, id, "close", 0, ct);
                            break;
                        }
                        default:
                            await SendAsync(relay, session, new { op = "error", msg = "unknown op" }, ct);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    try { await SendAsync(relay, session, new { op = "error", msg = ex.Message }, ct); } catch { }
                }
            }
        }
        finally
        {
            // Session ended — close out anything the viewer left open so the log isn't stuck "still open".
            foreach (var id in openIds)
                try { await relay.SendAuditAsync(session.PeerUuid, id, "close", 0, CancellationToken.None); } catch { }
        }
    }

    private static long SizeOf(IShareSource vault, string id)
    {
        try { using var s = vault.OpenSharedEntry(id); return s.Length; } catch { return 0; }
    }

    private static async Task HandleListAsync(RelayClient relay, IShareSource vault, SecureSession session, CancellationToken ct)
    {
        var entries = new List<object>();
        foreach (var e in vault.ShareEntries())
            entries.Add(new { id = e.BlobId, name = e.RelPath, size = e.Size, modified = e.ModifiedUtcTicks });
        await SendAsync(relay, session, new { op = "list", vault = vault.ShareName, entries }, ct);
        await relay.SendAuditAsync(session.PeerUuid, "(index)", "list", entries.Count, ct);
    }

    private static async Task HandleOpenAsync(RelayClient relay, IShareSource vault, SecureSession session, JsonElement root, CancellationToken ct)
    {
        var id = root.GetProperty("id").GetString() ?? "";
        var offset = root.GetProperty("offset").GetInt64();
        var length = root.TryGetProperty("length", out var l) ? l.GetInt32() : ChunkSize;
        length = Math.Min(length, ChunkSize);

        using var stream = vault.OpenSharedEntry(id);
        var total = stream.Length;

        if (offset >= total)
        {
            await SendAsync(relay, session, new { op = "chunk", id, offset, data = "", eof = true }, ct);
            return;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[(int)Math.Min(length, total - offset)];
        var read = await ReadFullyAsync(stream, buf, ct);
        var eof = offset + read >= total;
        await SendAsync(relay, session,
            new { op = "chunk", id, offset, data = Convert.ToBase64String(buf, 0, read), eof }, ct);
    }

    // ---------------- Viewer (browses + fetches from a host over one session) ----------------

    /// <summary>Lists the host's shared vault (name + entries).</summary>
    public static async Task<SharedListing> ListAsync(RelayClient relay, SecureSession session, CancellationToken ct)
    {
        await SendAsync(relay, session, new { op = "list" }, ct);
        using var doc = JsonDocument.Parse(await session.ReceiveAsync(relay, ct));
        var root = doc.RootElement;
        if (root.GetProperty("op").GetString() == "error")
            throw new InvalidOperationException(root.GetProperty("msg").GetString());

        var items = new List<SharedItem>();
        foreach (var e in root.GetProperty("entries").EnumerateArray())
            items.Add(new SharedItem
            {
                Id = e.GetProperty("id").GetString()!,
                Name = e.GetProperty("name").GetString()!,
                Size = e.GetProperty("size").GetInt64(),
                ModifiedUtcTicks = e.GetProperty("modified").GetInt64(),
            });
        var name = root.TryGetProperty("vault", out var v) ? v.GetString() ?? "" : "";
        return new SharedListing { VaultName = name, Items = items };
    }

    /// <summary>Streams a shared entry's bytes into <paramref name="dest"/>, reporting bytes received.</summary>
    public static async Task FetchAsync(RelayClient relay, SecureSession session, string id, long size,
        Stream dest, IProgress<long>? progress, CancellationToken ct)
    {
        long offset = 0;
        while (offset < size && !ct.IsCancellationRequested)
        {
            await SendAsync(relay, session, new { op = "open", id, offset, length = ChunkSize }, ct);
            using var doc = JsonDocument.Parse(await session.ReceiveAsync(relay, ct));
            var root = doc.RootElement;
            if (root.GetProperty("op").GetString() == "error")
                throw new InvalidOperationException(root.GetProperty("msg").GetString());

            var data = Convert.FromBase64String(root.GetProperty("data").GetString() ?? "");
            if (data.Length > 0)
            {
                await dest.WriteAsync(data, ct);
                offset += data.Length;
                progress?.Report(offset);
            }
            if (root.GetProperty("eof").GetBoolean() || data.Length == 0) break;
        }
    }

    /// <summary>Tells the host the viewer just opened an entry in its viewer (fire-and-forget). The host
    /// records this as an access ("open"); paired with <see cref="CloseAsync"/> it yields a duration.</summary>
    public static Task ViewAsync(RelayClient relay, SecureSession session, string id, CancellationToken ct = default)
        => SendAsync(relay, session, new { op = "view", id }, ct);

    /// <summary>Tells the host the viewer has finished viewing an entry (fire-and-forget; no response).</summary>
    public static Task CloseAsync(RelayClient relay, SecureSession session, string id, CancellationToken ct = default)
        => SendAsync(relay, session, new { op = "close", id }, ct);

    /// <summary>Convenience: fetch an entire entry into a byte array (for viewing small media in memory).</summary>
    public static async Task<byte[]> FetchAllAsync(RelayClient relay, SecureSession session, string id, long size, CancellationToken ct)
    {
        using var ms = new MemoryStream(checked((int)Math.Min(size, int.MaxValue)));
        await FetchAsync(relay, session, id, size, ms, null, ct);
        return ms.ToArray();
    }

    // ---------------- helpers ----------------

    private static Task SendAsync(RelayClient relay, SecureSession session, object msg, CancellationToken ct)
        => session.SendAsync(relay, Utf8(msg), ct);

    private static async Task<int> ReadFullyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
