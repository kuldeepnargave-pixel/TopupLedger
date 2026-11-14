using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;
using Xunit;

namespace TopupLedger.UnitTests;

/// <summary>
/// Verifies the commission test vectors from the assignment (Section 4.1).
/// All money values are integer fils; rounding is half-up.
/// </summary>
public class CommissionServiceTests
{
    private readonly CommissionService _svc = new();
    private readonly BusinessDayService _bd = new();

    // Helper: build the rule list identical to db/seed.sql
    private static List<CommissionRule> SeedRules() => new()
    {
        // 0.01-49.99 AED
        new() { Id = 1, FaceValueFromFils = 1,      FaceValueToFils = 4999,    RetailerRatePct = 2.000m, DistributorRatePct = 2.750m, EffectiveFromUtc = new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc), EffectiveToUtc = null },
        // 50-199.99 until 2026-09-30 Dubai (= 2026-09-30 20:00 UTC)
        new() { Id = 2, FaceValueFromFils = 5000,  FaceValueToFils = 19999,  RetailerRatePct = 2.350m, DistributorRatePct = 3.100m, EffectiveFromUtc = new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc), EffectiveToUtc = new DateTime(2026,9,30,20,0,0,DateTimeKind.Utc) },
        // 50-199.99 from 2026-10-01 Dubai
        new() { Id = 3, FaceValueFromFils = 5000,  FaceValueToFils = 19999,  RetailerRatePct = 2.500m, DistributorRatePct = 3.100m, EffectiveFromUtc = new DateTime(2026,9,30,20,0,0,DateTimeKind.Utc), EffectiveToUtc = null },
        // 200-2000
        new() { Id = 4, FaceValueFromFils = 20000, FaceValueToFils = 200000, RetailerRatePct = 2.600m, DistributorRatePct = 3.400m, EffectiveFromUtc = new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc), EffectiveToUtc = null }
    };

    [Theory]
    // Row 1 (solved in the assignment): 13.53 AED @ 2026-09-30T19:59:59Z -> retailer=27, distributor=10
    [InlineData(1353L, "2026-09-30T19:59:59Z", 27L, 10L, "2026-10-01")]
    // Row 2: 125.05 AED @ 2026-09-30T20:00:00Z (exactly at midnight Dubai = 2026-10-01 00:00 Dubai)
    //        -> new retailer rate 2.50% applies (effective_from_utc = 2026-09-30 20:00 UTC = 2026-10-01 00:00 Dubai)
    //        retailer = round(12505 * 2.5 / 100) = round(312.625) = 313
    //        distributor = round(12505 * 3.1 / 100) - 313 = round(387.655) - 313 = 388 - 313 = 75
    [InlineData(12505L, "2026-09-30T20:00:00Z", 313L, 75L, "2026-10-01")]
    // Row 3: 125.05 AED @ 2026-09-30T19:59:59Z (1 sec before midnight Dubai)
    //        -> OLD rate 2.35% applies (effective_to_utc exclusive boundary = 2026-09-30T20:00:00Z)
    //        retailer = round(12505 * 2.35 / 100) = round(293.8675) = 294
    //        distributor = round(12505 * 3.1 / 100) - 294 = 388 - 294 = 94
    [InlineData(12505L, "2026-09-30T19:59:59Z", 294L, 94L, "2026-10-01")]
    // Row 4: 49.99 AED @ 2026-10-11T18:45:00Z = 22:45 Dubai (after cutoff) -> business day 2026-10-12
    //        -> 2.00% bracket, retailer=round(4999*2/100)=100=1.00 AED
    //        distributor = round(4999*2.75/100) - 100 = round(137.4725) - 100 = 137 - 100 = 37
    [InlineData(4999L, "2026-10-11T18:45:00Z", 100L, 37L, "2026-10-12")]
    // Row 5: 200.00 AED @ 2026-10-11T19:40:00Z = 23:40 Dubai (after cutoff) -> business day 2026-10-12
    //        -> 2.60% bracket
    //        retailer = round(20000*2.6/100) = round(520) = 520
    //        distributor = round(20000*3.4/100) - 520 = 680 - 520 = 160
    [InlineData(20000L, "2026-10-11T19:40:00Z", 520L, 160L, "2026-10-12")]
    // Row 6: 109.80 AED @ 2026-10-11T18:15:00Z = 22:15 Dubai (after cutoff) -> business day 2026-10-12
    //        -> 2.50% bracket
    //        retailer = round(10980*2.5/100) = round(274.5) = 275 (half-up)
    //        distributor = round(10980*3.1/100) - 275 = round(340.38) - 275 = 340 - 275 = 65
    [InlineData(10980L, "2026-10-11T18:15:00Z", 275L, 65L, "2026-10-12")]
    public void Commission_TestVectors_MatchAssignment(long faceValueFils, string utcTime,
        long expectedRetailer, long expectedDistributor, string expectedBusinessDateYyyyMmDd)
    {
        var txTime = DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var rule = _svc.ResolveRule(SeedRules(), faceValueFils, txTime);
        var breakdown = _svc.Compute(faceValueFils, rule);

        Assert.Equal(expectedRetailer, breakdown.RetailerCommissionFils);
        Assert.Equal(expectedDistributor, breakdown.DistributorShareFils);

        var businessDay = _bd.GetBusinessDayUtc(txTime);
        Assert.Equal(expectedBusinessDateYyyyMmDd, businessDay.ToString("yyyy-MM-dd"));
    }

    [Theory]
    [InlineData(0L, false)]      // below minimum (0.01 AED = 1 fils)
    [InlineData(200001L, false)] // above maximum (2000.01 AED)
    public void Commission_OutOfRange_Rejected(long faceValueFils, bool _)
    {
        Assert.Throws<DomainException>(() =>
            _svc.ResolveRule(SeedRules(), faceValueFils, DateTime.UtcNow));
    }

    [Fact]
    public void Commission_HalfUpRounding_RoundsAtPointFive()
    {
        Assert.Equal(275, CommissionService.RoundHalfUp(274.5m));
        Assert.Equal(274, CommissionService.RoundHalfUp(274.4m));
        // Math.Round with AwayFromZero rounds -0.5 to -1 (away from zero, not up toward positive).
        // Money values are always positive in our system; this is purely a test for completeness.
        Assert.Equal(-1, CommissionService.RoundHalfUp(-0.5m));
        Assert.Equal(1, CommissionService.RoundHalfUp(0.5m));
    }

    [Fact]
    public void Commission_WalletDebit_EqualsFaceValueMinusRetailerCommission()
    {
        var rule = _svc.ResolveRule(SeedRules(), 20000, new DateTime(2026, 10, 11, 19, 40, 0, DateTimeKind.Utc));
        var b = _svc.Compute(20000, rule);
        Assert.Equal(20000 - b.RetailerCommissionFils, b.WalletDebitFils);
    }
}
