using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Galileo.Services;

/// <summary>
/// A Google <see cref="IDataStore"/> that keeps the OAuth refresh token <b>DPAPI-encrypted</b> on disk
/// (per Windows user) instead of the plaintext JSON that <c>FileDataStore</c> writes. Only the same
/// Windows account can decrypt it, so a copy of <c>%LocalAppData%</c> (roaming/backup) or another
/// profile can't lift the token.
/// </summary>
public sealed class DpapiDataStore : IDataStore
{
    private readonly string _dir;

    public DpapiDataStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        // Remove any legacy plaintext token files left by a previous FileDataStore (one-time migration —
        // the user just re-signs-in). Only our own *.dpapi files are kept.
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!f.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private string PathFor(string key)
        => Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dpapi");

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var prot = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), prot);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            try
            {
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
                return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
            }
            catch { /* corrupt/undecryptable → treat as absent (forces re-auth) */ }
        }
        return Task.FromResult<T>(default!);
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        try { foreach (var f in Directory.EnumerateFiles(_dir, "*.dpapi")) File.Delete(f); } catch { }
        return Task.CompletedTask;
    }
}
