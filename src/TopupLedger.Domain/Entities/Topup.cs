using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// A top-up transaction. All money columns are integer fils.
/// Status transitions: PENDING -> SUCCESS | FAILED | EXPIRED. Terminal states never change.
/// </summary>
public class Topup
{
    public long Id { get; set; }
    public long PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public long WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;

    public string ClientRef { get; set; } = string.Empty;   // partner-supplied reference, unique per partner
    public string MsisdnNormalized { get; set; } = string.Empty;  // 9715XXXXXXXX
    public string MsisdnMasked { get; set; } = string.Empty;       // 97150***567 for logging
    public Operator Operator { get; set; }

    public long FaceValueFils { get; set; }          // face value, fils
    public long RetailerCommissionFils { get; set; } // rounded retailer commission
    public long DistributorShareFils { get; set; }   // rounded distributor commission - retailer commission
    public long WalletDebitFils { get; set; }        // FaceValueFils - RetailerCommissionFils

    public TopupStatus Status { get; set; } = TopupStatus.Pending;

    /// <summary>
    /// Operator-assigned transaction id (from callback). NULL until the operator calls back.
    /// </summary>
    public string? OperatorTxnId { get; set; }

    public DateTime CreatedAtUtc { get; set; }            // request received (UTC)
    public DateTime BusinessDateUtc { get; set; }         // midnight UTC of the business day (see BusinessDayService)
    public DateTime? FinalStateAtUtc { get; set; }        // when terminal state was reached
    public DateTime? CallbackReceivedAtUtc { get; set; } // first callback received

    public List<WalletLedger> LedgerEntries { get; set; } = new();
    public List<CallbackHistory> CallbackHistory { get; set; } = new();
}
