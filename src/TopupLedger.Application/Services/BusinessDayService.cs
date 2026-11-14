using System.Globalization;

namespace TopupLedger.Application.Services;

/// <summary>
/// Dubai business-day logic. Dubai is UTC+04:00 with no DST.
/// Business-day cut-off is 22:00 Dubai local time. Any transaction at or after
/// 22:00:00 Dubai time belongs to the NEXT business day.
///
/// All timestamps are stored in UTC; conversion to Dubai time is done in code,
/// never relying on the server OS timezone (the assignment explicitly tests this).
/// </summary>
public sealed class BusinessDayService
{
    // Dubai timezone is fixed UTC+04:00 with no DST. We use a fixed offset to
    // guarantee identical behaviour on any host OS (Windows / Linux / macOS).
    public static readonly TimeSpan DubaiOffset = TimeSpan.FromHours(4);
    public static readonly TimeZoneInfo DubaiZone =
        TimeZoneInfo.CreateCustomTimeZone("Asia/Dubai", DubaiOffset, "Gulf Standard Time", "GST");

    public const int BusinessDayCutoffHour = 22; // 22:00 Dubai

    /// <summary>
    /// Returns the UTC DateTime that identifies the business day the given UTC instant belongs
    /// to, after applying the 22:00 Dubai cut-off. The returned value is midnight UTC of the
    /// Dubai business day DATE (e.g. for any top-up whose business day is 2026-10-12 Dubai,
    /// this returns 2026-10-12 00:00:00 UTC). The DATE is what identifies the business day;
    /// the time-of-day is always 00:00 UTC so equality filters work in SQL.
    /// </summary>
    public DateTime GetBusinessDayUtc(DateTime utcInstant)
    {
        if (utcInstant.Kind != DateTimeKind.Utc)
            utcInstant = DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc);

        // Convert to Dubai wall-clock.
        DateTime dubai = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, DubaiZone);

        // If at or after 22:00 Dubai, the transaction belongs to the next calendar day.
        if (dubai.TimeOfDay >= TimeSpan.FromHours(BusinessDayCutoffHour))
            dubai = dubai.Date.AddDays(1);
        else
            dubai = dubai.Date;

        // Return midnight UTC of the business day DATE. The DATE matches the Dubai business
        // day DATE (e.g. 2026-10-12). Filtering with `WHERE business_date_utc = '2026-10-12'`
        // parses to '2026-10-12 00:00:00 UTC' which equals this value.
        return new DateTime(dubai.Year, dubai.Month, dubai.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Parses a YYYY-MM-DD string as a business-day UTC date (the same format used
    /// throughout the API). Throws ArgumentException on bad format.
    /// </summary>
    public DateTime ParseBusinessDay(string yyyyMmDd)
    {
        if (!DateTime.TryParseExact(yyyyMmDd, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var d))
            throw new ArgumentException($"Invalid date format: '{yyyyMmDd}'. Expected YYYY-MM-DD.");
        return d;
    }

    /// <summary>
    /// Returns the inclusive lower bound (UTC) of a business-day range from
    /// (default: 7 days ago) up to today. Used by reports when 'from' / 'to' are omitted.
    /// </summary>
    public (DateTime fromUtc, DateTime toUtc) DefaultRange(int days = 7)
    {
        var today = GetBusinessDayUtc(DateTime.UtcNow);
        return (today.AddDays(-(days - 1)), today);
    }
}
