using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// B2B partner (grocery, phone shop, exchange house). Has API key, secret and a prepaid wallet.
/// The API secret is stored AES-GCM encrypted (see Infrastructure/Secrets/PartnerSecretProtector).
/// </summary>
public class Partner
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;          // public, used in X-Api-Key
    public byte[] SecretCipher { get; set; } = Array.Empty<byte>(); // AES-GCM ciphertext of HMAC secret
    public byte[] SecretNonce { get; set; } = Array.Empty<byte>(); // AES-GCM nonce (12 bytes)
    public byte[] SecretTag { get; set; } = Array.Empty<byte>();   // AES-GCM auth tag (16 bytes)
    public long? DistributorId { get; set; }                      // optional parent distributor (self-reference)
    public Partner? Distributor { get; set; }
    public PartnerStatus Status { get; set; } = PartnerStatus.Active;
    public Wallet? Wallet { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
}
