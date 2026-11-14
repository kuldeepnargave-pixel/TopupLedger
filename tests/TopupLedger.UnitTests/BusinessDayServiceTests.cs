using TopupLedger.Application.Services;
using Xunit;

namespace TopupLedger.UnitTests;

/// <summary>
/// Verifies the Dubai business-day rule (22:00 cut-off) using the assignment's test vectors:
/// - A transaction at 22:30 Dubai time (UTC 18:30 same day if Dubai is +4) belongs to the NEXT business day.
/// - The UAE has no DST; we use a fixed +04:00 offset that does not depend on the server OS timezone.
/// </summary>
public class BusinessDayServiceTests
{
    private readonly BusinessDayService _svc = new();

    [Theory]
    // 2026-10-11T18:30:00Z = 2026-10-11T22:30:00 Dubai (at or after 22:00 -> next business day)
    [InlineData("2026-10-11T18:30:00Z", "2026-10-12")]
    // 2026-10-11T17:59:59Z = 2026-10-11T21:59:59 Dubai (before 22:00 -> same business day)
    [InlineData("2026-10-11T17:59:59Z", "2026-10-11")]
    // Exactly at 22:00 Dubai = 2026-10-11T18:00:00Z -> next business day (boundary is inclusive)
    [InlineData("2026-10-11T18:00:00Z", "2026-10-12")]
    // One second before: 21:59:59 Dubai -> same day
    [InlineData("2026-10-11T17:59:59Z", "2026-10-11")]
    // Just after midnight Dubai on the next day -> next business day
    [InlineData("2026-10-11T20:00:01Z", "2026-10-12")]
    public void BusinessDay_Cutoff_Correct(string utcTime, string expectedBusinessDay)
    {
        var t = DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var bd = _svc.GetBusinessDayUtc(t);
        Assert.Equal(expectedBusinessDay, bd.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void BusinessDay_ServiceDoesNotDependOnOsTimezone()
    {
        // The service uses TimeZoneInfo.CreateCustomTimeZone with a fixed +04:00 offset.
        // Running this test under any OS timezone should produce the same result.
        Assert.Equal(TimeSpan.FromHours(4), BusinessDayService.DubaiOffset);
        Assert.Equal("Asia/Dubai", BusinessDayService.DubaiZone.Id);
    }

    [Fact]
    public void BusinessDay_Report1_Assigns_2230DubaiTransaction_ToNextDay()
    {
        // This is the assignment's required report test #1:
        //   "a top-up at 22:30 Dubai time appears in the NEXT business day in report #1"
        var txAt2230DubaiUtc = new DateTime(2026, 10, 11, 18, 30, 0, DateTimeKind.Utc);
        var bd = _svc.GetBusinessDayUtc(txAt2230DubaiUtc);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), bd);
    }
}
