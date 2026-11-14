using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// End-of-business-day summary per partner. Generating a settlement twice for the same
/// (partnerId, businessDay) must give the same result with no duplicate rows; uniqueness
/// is enforced by a unique index on (PartnerId, BusinessDateUtc).
/// </summary>
public class Settlement
{
    public long Id { get; set; }
    public long PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public DateTime BusinessDateUtc { get; set; }   // midnight UTC of business day (Dubai-adjusted)
    public long SuccessCount { get; set; }
    public long FailedCount { get; set; }
    public long ExpiredCount { get; set; }
    public long PendingCount { get; set; }       // not normally expected at settlement time
    public long TotalFaceValueFils { get; set; }     // sum of face value for SUCCESS top-ups
    public long TotalRetailerCommissionFils { get; set; }
    public long TotalDistributorShareFils { get; set; }
    public long NetDebitFils { get; set; }            // totalFaceValue - retailerCommission
    public DateTime GeneratedAtUtc { get; set; }
    public List<SettlementItem> Items { get; set; } = new();
}

public class SettlementItem
{
    public long Id { get; set; }
    public long SettlementId { get; set; }
    public Settlement Settlement { get; set; } = null!;
    public long TopupId { get; set; }
    public long FaceValueFils { get; set; }
    public long RetailerCommissionFils { get; set; }
    public long DistributorShareFils { get; set; }
    public TopupStatus Status { get; set; }
    public Operator Operator { get; set; }
}
