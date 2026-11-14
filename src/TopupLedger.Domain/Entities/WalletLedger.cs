using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Immutable append-only ledger row. Double-entry: every money movement writes
/// balanced credit/debit entries. Rows are NEVER updated or deleted; refunds create
/// new compensating rows with reason TopupRefund.
/// </summary>
public class WalletLedger
{
    public long Id { get; set; }
    public long WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;
    public long TopupId { get; set; }        // nullable logically (0 = no topup); manual credits use TopupId = 0
    public Topup? Topup { get; set; }
    public LedgerEntryType EntryType { get; set; }   // CREDIT or DEBIT
    public LedgerReason Reason { get; set; }
    public long AmountFils { get; set; }      // always positive; sign is implied by EntryType
    public long RunningBalanceFils { get; set; }   // balance immediately after this entry was applied
    public string? ReasonText { get; set; }    // for manual credit: admin's reason text
    public string? CorrelationId { get; set; }  // ties to request correlation id
    public DateTime CreatedAtUtc { get; set; }
}
