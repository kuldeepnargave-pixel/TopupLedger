using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TopupLedger.Application.Interfaces;
using TopupLedger.Infrastructure.Configuration;

namespace TopupLedger.Infrastructure.Secrets;

/// <summary>
/// Protects partner HMAC secrets at rest using AES-GCM (96-bit nonce, 128-bit auth tag).
/// The master key is read from configuration (TOPUPLEDGER_SECRET_MASTER_KEY environment
/// variable, or appsettings:Secrets:MasterKey in development). The master key MUST be
/// exactly 32 bytes (256-bit). In development we generate one if missing and log a
/// prominent warning; in production a missing key throws on startup.
///
/// Plaintext secrets never touch the database. Decryption happens only inside the
/// HmacAuthMiddleware at request-verification time, in memory.
/// </summary>
public sealed class AesGcmPartnerSecretProtector : IPartnerSecretProtector
{
    private readonly byte[] _masterKey;

    public AesGcmPartnerSecretProtector(IOptions<SecretsOptions> options)
    {
        var configured = options.Value.MasterKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Dev-only fallback: deterministic dev key so seeded partners work without env var.
            // PRODUCTION MUST set TOPUPLEDGER_SECRET_MASTER_KEY. See README.
            configured = "DEV_ONLY_MASTER_KEY_DO_NOT_USE_IN_PRODUCTION_32B!";
            Console.WriteLine("WARNING: TOPUPLEDGER_SECRET_MASTER_KEY is not set. Using insecure dev-only master key. DO NOT USE IN PRODUCTION.");
        }

        byte[] keyBytes = Encoding.UTF8.GetBytes(configured);
        if (keyBytes.Length != 32)
        {
            // For dev convenience, derive a 32-byte key via SHA256 so users can supply any string.
            using var sha = SHA256.Create();
            keyBytes = sha.ComputeHash(keyBytes);
        }
        _masterKey = keyBytes;
    }

    public (byte[] cipher, byte[] nonce, byte[] tag) Encrypt(string plaintextSecret)
    {
        if (string.IsNullOrEmpty(plaintextSecret))
            throw new ArgumentException("Secret cannot be empty.", nameof(plaintextSecret));

        byte[] plaintext = Encoding.UTF8.GetBytes(plaintextSecret);
        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];      // 16 bytes
        byte[] cipher = new byte[plaintext.Length];

        using var aes = new AesGcm(_masterKey, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return (cipher, nonce, tag);
    }

    public string Decrypt(byte[] cipher, byte[] nonce, byte[] tag)
    {
        if (cipher == null || nonce == null || tag == null) return string.Empty;
        byte[] plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(_masterKey, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
