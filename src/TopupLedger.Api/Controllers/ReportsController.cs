using Microsoft.AspNetCore.Mvc;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Services;
using TopupLedger.Infrastructure.Services;

namespace TopupLedger.Api.Controllers;

/// <summary>
/// The 10 read-only report APIs. Authenticated with X-Admin-Key (configured via env or appsettings).
/// Partner HMAC keys must NOT work here (handled by AdminApiKeyMiddleware).
/// </summary>
[ApiController]
[Route("api/v1/reports")]
public class ReportsController : ControllerBase
{
    private readonly ReportService _reports;
    private readonly BusinessDayService _businessDays;
    private readonly TimeProvider _time;

    public ReportsController(ReportService reports, BusinessDayService businessDays, TimeProvider time)
    {
        _reports = reports;
        _businessDays = businessDays;
        _time = time;
    }

    [HttpGet("daily-summary")]
    public async Task<IActionResult> DailySummary([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format)
        => await RunReport(() => _reports.DailySummaryAsync(from, to, HttpContext.RequestAborted), format);

    [HttpGet("operators")]
    public async Task<IActionResult> Operators([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format)
        => await RunReport(() => _reports.OperatorsAsync(from, to, HttpContext.RequestAborted), format);

    [HttpGet("partners/top")]
    public async Task<IActionResult> PartnersTop([FromQuery] string? from, [FromQuery] string? to, [FromQuery] int limit = 10, [FromQuery] string? format = null)
        => await RunReport(() => _reports.PartnersTopAsync(from, to, limit, HttpContext.RequestAborted), format);

    [HttpGet("commission")]
    public async Task<IActionResult> Commission([FromQuery] string? from, [FromQuery] string? to, [FromQuery] long? partnerId, [FromQuery] string? format)
        => await RunReport(() => _reports.CommissionAsync(from, to, partnerId, HttpContext.RequestAborted), format);

    [HttpGet("wallets")]
    public async Task<IActionResult> WalletsAsOf([FromQuery] string asOf, [FromQuery] string? format)
    {
        if (!DateTime.TryParse(asOf, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var asOfUtc))
            throw new DomainException("VALIDATION_FAILED", "asOf must be an ISO date-time.", httpStatusCode: 400);
        return await RunReport(() => _reports.WalletsAsOfAsync(asOfUtc, HttpContext.RequestAborted), format);
    }

    [HttpGet("wallets/{partnerId:long}/statement")]
    public async Task<IActionResult> WalletStatement(long partnerId, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format)
    {
        var result = await _reports.WalletStatementAsync(partnerId, from, to, HttpContext.RequestAborted);
        if (format == "csv")
        {
            var csv = ReportService.ToCsv((List<Dictionary<string, object>>)result["entries"]);
            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "wallet_statement.csv");
        }
        return Ok(result);
    }

    [HttpGet("hourly")]
    public async Task<IActionResult> Hourly([FromQuery] string businessDay, [FromQuery] string? format)
    {
        if (string.IsNullOrEmpty(businessDay))
        {
            // Default to today's business day.
            businessDay = _businessDays.GetBusinessDayUtc(_time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
        }
        return await RunReport(() => _reports.HourlyAsync(businessDay, HttpContext.RequestAborted), format);
    }

    [HttpGet("failures")]
    public async Task<IActionResult> Failures([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format)
        => await RunReport(() => _reports.FailuresAsync(from, to, HttpContext.RequestAborted), format);

    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format)
        => await RunReport(() => _reports.ReconciliationAsync(from, to, HttpContext.RequestAborted), format);

    [HttpGet("partners/inactive")]
    public async Task<IActionResult> PartnersInactive([FromQuery] int days = 7, [FromQuery] string? format = null)
        => await RunReport(() => _reports.PartnersInactiveAsync(days, HttpContext.RequestAborted), format);

    private async Task<IActionResult> RunReport(Func<Task<List<Dictionary<string, object>>>> fn, string? format)
    {
        var rows = await fn();
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = ReportService.ToCsv(rows);
            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "report.csv");
        }
        return Ok(rows);
    }
}
