using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Galileo.Services;

/// <summary>
/// Windows Hello / TPM-backed keyslot for a vault. Uses <see cref="KeyCredentialManager"/> to create a
/// per-vault key gated by Hello, then signs a fixed per-vault challenge to derive a stable wrapping
/// key (RSA PKCS#1 signatures over the same data are deterministic). This is distinct from
/// <c>HelloAuth</c>, which only performs a yes/no consent check for the (unencrypted) Hidden album.
/// </summary>
public static class HelloKey
{
    private static readonly byte[] HkdfInfo = System.Text.Encoding.UTF8.GetBytes("galileo-vault-hello-v1");

    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>Enrolls (creates) a Hello key and returns a 256-bit KEK derived from signing the
    /// challenge, or null if Hello is unavailable / the user cancels.</summary>
    public static async Task<byte[]?> EnrollAndDeriveAsync(string keyName, byte[] challenge)
    {
        try
        {
            var create = await KeyCredentialManager.RequestCreateAsync(keyName, KeyCredentialCreationOption.ReplaceExisting);
            return create.Status != KeyCredentialStatus.Success ? null : await SignDeriveAsync(create.Credential, challenge);
        }
        catch { return null; }
    }

    /// <summary>Opens an existing Hello key and re-derives its KEK by signing the challenge (prompts
    /// the user for Hello), or null on failure / cancel.</summary>
    public static async Task<byte[]?> OpenAndDeriveAsync(string keyName, byte[] challenge)
    {
        try
        {
            var open = await KeyCredentialManager.OpenAsync(keyName);
            return open.Status != KeyCredentialStatus.Success ? null : await SignDeriveAsync(open.Credential, challenge);
        }
        catch { return null; }
    }

    public static async Task DeleteAsync(string keyName)
    {
        try { await KeyCredentialManager.DeleteAsync(keyName); }
        catch { /* ignore */ }
    }

    private static async Task<byte[]?> SignDeriveAsync(KeyCredential credential, byte[] challenge)
    {
        var op = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (op.Status != KeyCredentialStatus.Success) return null;

        CryptographicBuffer.CopyToByteArray(op.Result, out var signature);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, VaultCrypto.KeySize, salt: null, info: HkdfInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}
