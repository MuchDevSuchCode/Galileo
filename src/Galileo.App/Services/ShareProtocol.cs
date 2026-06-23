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
    IReadOnlyList<VaultEntry> ShareEntries();
    Stream OpenSharedEntry(string blobId);
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
                        await HandleOpenAsync(relay, vault, session, root, ct);
                        break;
                    case "close":
                        // Viewer finished viewing a file — record a close event (no response). Pairing
                        // open/close on the host lets it show how long each file was open.
                        await relay.SendAuditAsync(session.PeerUuid, root.GetProperty("id").GetString() ?? "", "close", 0, ct);
                        break;
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

    private static async Task HandleListAsync(RelayClient relay, IShareSource vault, SecureSession session, CancellationToken ct)
    {
        var entries = new List<object>();
        foreach (var e in vault.ShareEntries())
            entries.Add(new { id = e.BlobId, name = e.RelPath, size = e.Size, modified = e.ModifiedUtcTicks });
        await SendAsync(relay, session, new { op = "list", entries }, ct);
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

        // Audit the access once, when the viewer starts reading the file (offset 0).
        if (offset == 0)
            await relay.SendAuditAsync(session.PeerUuid, id, "open", total, ct);

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

    /// <summary>Lists the host's shared entries.</summary>
    public static async Task<IReadOnlyList<SharedItem>> ListAsync(RelayClient relay, SecureSession session, CancellationToken ct)
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
        return items;
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

    /// <summary>Tells the host the viewer has finished with an entry (fire-and-forget; no response).
    /// The host records a "close" audit event so it can report how long the file was open.</summary>
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
