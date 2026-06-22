using System;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Galileo.Services;

/// <summary>
/// A peer's cryptographic identity for secure P2P sharing, deterministically derived from a wallet-style
/// BIP39 seed phrase (so it can be backed up and recovered on another device):
///  - Ed25519 keypair for signing (authenticating who you are to the relay / the other peer),
///  - X25519 keypair for key agreement (ECDH → per-session keys for end-to-end file encryption),
///  - a stable UUID for discovery, and a short fingerprint ("safety number") for out-of-band verification.
/// The relay and the other peer only ever see public keys; the seed/private keys never leave this device.
/// </summary>
public sealed class PeerKeys
{
    public required byte[] SignPublic { get; init; }   // Ed25519 public (32)
    public required byte[] SignPrivate { get; init; }  // Ed25519 private seed (32)
    public required byte[] AgreePublic { get; init; }  // X25519 public (32)
    public required byte[] AgreePrivate { get; init; } // X25519 private (32)
    public required Guid Uuid { get; init; }
    public required string Fingerprint { get; init; }  // grouped base32 safety number

    public void Wipe()
    {
        CryptographicOperations.ZeroMemory(SignPrivate);
        CryptographicOperations.ZeroMemory(AgreePrivate);
    }
}

public static class PeerIdentity
{
    private const string SignInfo = "galileo-p2p-sign-v1";
    private const string AgreeInfo = "galileo-p2p-agree-v1";
    private const string UuidInfo = "galileo-p2p-uuid-v1";

    // ---- seed phrase ----

    /// <summary>Generates a new BIP39 seed phrase (12 words = 128-bit). Show once, store offline.</summary>
    public static string GenerateSeedPhrase(int words = 12)
        => new Mnemonic(Wordlist.English, words == 24 ? WordCount.TwentyFour : WordCount.Twelve).ToString();

    /// <summary>True when the phrase is a valid BIP39 mnemonic (word list + checksum).</summary>
    public static bool ValidateSeedPhrase(string phrase)
    {
        try { return new Mnemonic(phrase?.Trim() ?? "", Wordlist.English).IsValidChecksum; }
        catch { return false; }
    }

    // ---- identity derivation ----

    /// <summary>Derives the full identity (keys + UUID + fingerprint) from a seed phrase. An optional
    /// passphrase (BIP39 "25th word") yields a different identity from the same words.</summary>
    public static PeerKeys FromSeedPhrase(string phrase, string? passphrase = null)
    {
        var mnemonic = new Mnemonic(phrase.Trim(), Wordlist.English);
        if (!mnemonic.IsValidChecksum) throw new ArgumentException("Invalid seed phrase.");
        var seed = mnemonic.DeriveSeed(passphrase); // 64 bytes
        try { return FromSeed(seed); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static PeerKeys FromSeed(byte[] seed)
    {
        var signSeed = Hkdf(seed, SignInfo, 32);
        var agreePriv = Hkdf(seed, AgreeInfo, 32);
        try
        {
            var signSk = new Ed25519PrivateKeyParameters(signSeed, 0);
            var signPk = signSk.GeneratePublicKey().GetEncoded();

            var agreeSk = new X25519PrivateKeyParameters(agreePriv, 0);
            var agreePk = agreeSk.GeneratePublicKey().GetEncoded();

            return new PeerKeys
            {
                SignPublic = signPk,
                SignPrivate = (byte[])signSeed.Clone(),
                AgreePublic = agreePk,
                AgreePrivate = (byte[])agreePriv.Clone(),
                Uuid = DeriveUuid(signPk),
                Fingerprint = DeriveFingerprint(signPk, agreePk),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signSeed);
            CryptographicOperations.ZeroMemory(agreePriv);
        }
    }

    /// <summary>The shared 32-byte secret from my agreement private key and the peer's agreement public key
    /// (identical on both ends). Feed through HKDF per session for the actual file-encryption key.</summary>
    public static byte[] AgreeSharedSecret(byte[] myAgreePrivate, byte[] theirAgreePublic)
    {
        var agree = new X25519Agreement();
        agree.Init(new X25519PrivateKeyParameters(myAgreePrivate, 0));
        var secret = new byte[agree.AgreementSize];
        agree.CalculateAgreement(new X25519PublicKeyParameters(theirAgreePublic, 0), secret, 0);
        return secret;
    }

    /// <summary>Signs data with the Ed25519 identity key (proves "I am this UUID" to the relay/peer).</summary>
    public static byte[] Sign(byte[] signPrivate, byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(signPrivate, 0));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] signPublic, byte[] data, byte[] signature)
    {
        try
        {
            var v = new Ed25519Signer();
            v.Init(false, new Ed25519PublicKeyParameters(signPublic, 0));
            v.BlockUpdate(data, 0, data.Length);
            return v.VerifySignature(signature);
        }
        catch { return false; }
    }

    // ---- deniable storage primitive ----
    // Encrypts the seed phrase under a passphrase into an opaque blob: salt(16) || GCM(nonce||tag||cipher).
    // No labels, magic bytes, or structure that identifies it as a Galileo identity — it's indistinguishable
    // from any other random-named encrypted blob in the vault store.

    public static byte[] Export(string phrase, string passphrase)
    {
        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.DeriveKey(passphrase, salt);
        try
        {
            var sealed_ = VaultCrypto.Encrypt(kek, Encoding.UTF8.GetBytes(phrase.Trim()));
            var outp = new byte[salt.Length + sealed_.Length];
            Buffer.BlockCopy(salt, 0, outp, 0, salt.Length);
            Buffer.BlockCopy(sealed_, 0, outp, salt.Length, sealed_.Length);
            return outp;
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    /// <summary>Recovers the identity from an <see cref="Export"/> blob. Throws on a wrong passphrase.</summary>
    public static PeerKeys Import(byte[] blob, string passphrase)
    {
        if (blob.Length <= VaultCrypto.SaltSize) throw new ArgumentException("Corrupt identity blob.");
        var salt = blob[..VaultCrypto.SaltSize];
        var sealed_ = blob[VaultCrypto.SaltSize..];
        var kek = VaultCrypto.DeriveKey(passphrase, salt);
        try
        {
            var phraseBytes = VaultCrypto.Decrypt(kek, sealed_); // throws on wrong passphrase
            return FromSeedPhrase(Encoding.UTF8.GetString(phraseBytes));
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    // ---- helpers ----

    private static byte[] Hkdf(byte[] ikm, string info, int length)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        hkdf.Init(new HkdfParameters(ikm, null, Encoding.ASCII.GetBytes(info)));
        var outp = new byte[length];
        hkdf.GenerateBytes(outp, 0, length);
        return outp;
    }

    private static Guid DeriveUuid(byte[] signPublic)
    {
        var h = SHA256.HashData(Concat(Encoding.ASCII.GetBytes(UuidInfo), signPublic));
        var b = h[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x50); // RFC 4122 version 5 (name-based)
        b[8] = (byte)((b[8] & 0x3F) | 0x80); // variant
        return new Guid(b);
    }

    private static string DeriveFingerprint(byte[] signPublic, byte[] agreePublic)
    {
        var h = SHA256.HashData(Concat(signPublic, agreePublic));
        var b32 = Base32(h[..20]); // 160 bits → 32 chars
        var sb = new StringBuilder();
        for (var i = 0; i < b32.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(b32[i]);
        }
        return sb.ToString();
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private const string B32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; // RFC 4648
    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var x in data)
        {
            buffer = (buffer << 8) | x;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(B32Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) sb.Append(B32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
