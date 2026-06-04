using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>
/// Owns the set of vaults on disk and the single currently-unlocked vault. Locking is centralized
/// here so the idle timer, the app-exit hook, and manual lock all go through one path.
/// </summary>
public sealed class VaultManager
{
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");

    public static string VaultsRoot => Path.Combine(AppDataRoot, "Vaults");

    /// <summary>The currently unlocked vault, or null when everything is locked.</summary>
    public Vault? Current { get; private set; }

    public bool IsAnyUnlocked => Current is not null;

    public IReadOnlyList<Vault> List()
    {
        var list = new List<Vault>();
        if (!Directory.Exists(VaultsRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(VaultsRoot))
        {
            try { list.Add(Vault.Load(dir)); }
            catch { /* skip an unreadable/partial vault */ }
        }
        return list.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Creates a vault, optionally enrolls Windows Hello, optionally moves the given
    /// files/folders into it (originals securely removed), then locks it.</summary>
    public async Task<Vault> CreateAsync(string name, string passphrase, bool useHello, IEnumerable<string>? importPaths)
    {
        Directory.CreateDirectory(VaultsRoot);
        var v = Vault.Create(VaultsRoot, name, passphrase); // returns unlocked
        try
        {
            if (useHello) await v.EnableHelloAsync();
            if (importPaths is not null) await v.ImportPathsAsync(importPaths, deleteOriginals: true);
        }
        finally { await v.LockAsync(); }
        return v;
    }

    public async Task UnlockWithPassphraseAsync(Vault v, string passphrase)
    {
        if (Current is not null && Current.Id != v.Id) await LockCurrentAsync();
        await v.UnlockWithPassphraseAsync(passphrase);
        Current = v;
    }

    /// <summary>Encrypts the given paths into the currently-unlocked vault and securely wipes the
    /// originals. Returns the number of files added (0 if nothing is unlocked).</summary>
    public Task<int> AddToCurrentAsync(IEnumerable<string> paths) =>
        Current is null ? Task.FromResult(0) : Current.AddToOpenVaultAsync(paths, deleteOriginals: true);

    public async Task<bool> UnlockWithHelloAsync(Vault v)
    {
        if (Current is not null && Current.Id != v.Id) await LockCurrentAsync();
        var ok = await v.UnlockWithHelloAsync();
        if (ok) Current = v;
        return ok;
    }

    /// <summary>Marks a vault as the active unlocked one after it was opened via another keyslot (Hello).</summary>
    public void SetCurrent(Vault v) => Current = v;

    public async Task LockCurrentAsync()
    {
        var c = Current;
        Current = null;
        if (c is not null) await c.LockAsync();
    }

    /// <summary>Crash recovery: at startup, securely wipe any leftover decrypted working folders.</summary>
    public void WipeOrphanWorkDirs()
    {
        var work = Vault.WorkRoot;
        if (!Directory.Exists(work)) return;
        foreach (var d in Directory.EnumerateDirectories(work))
            VaultCrypto.WipeDirectory(d);
        try { Directory.Delete(work, true); } catch { /* ignore */ }
    }
}
