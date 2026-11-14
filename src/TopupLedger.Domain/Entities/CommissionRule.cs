using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Commission rule applicable for a face value range, effective from a given instant (Dubai time).
/// "EffectiveFrom" is inclusive; the next rule with a later EffectiveFrom implicitly defines
/// the "to" boundary as exclusive.
/// </summary>
public class CommissionRule
{
    public long Id { get; set; }
    public long FaceValueFromFils { get; set; }  // inclusive
    public long FaceValueToFils { get; set; }    // inclusive upper bound
    public decimal RetailerRatePct { get; set; }  // 2.35m means 2.35%
    public decimal DistributorRatePct { get; set; }
    public DateTime EffectiveFromUtc { get; set; }  // inclusive; converted from Dubai local time
    public DateTime? EffectiveToUtc { get; set; }   // exclusive; null = open-ended
    public DateTime CreatedAtUtc { get; set; }
}
