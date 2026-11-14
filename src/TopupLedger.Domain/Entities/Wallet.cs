using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Partner prepaid wallet. All money stored as integer fils (BIGINT / long).
/// Balance MUST always equal the sum of WalletLedger entries for this wallet.
/// The Balance column is a denormalised cache for fast reads; it is updated
/// inside the same DB transaction as the ledger writes and protected by
/// SELECT ... FOR UPDATE on this row (pessimistic locking) for every debit/refund.
/// </summary>
public class Wallet
{
    public long Id { get; set; }
    public long PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public long BalanceFils { get; set; }  // current balance, fils (1 AED = 100 fils)
    public string Currency { get; set; } = "AED";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
