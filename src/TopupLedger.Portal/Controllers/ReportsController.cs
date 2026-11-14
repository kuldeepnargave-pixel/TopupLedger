using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Application.Services;
using TopupLedger.Infrastructure.Services;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

/// <summary>
/// Reports panel. One page per report. The same ReportService is used by both the portal and
/// the JSON/CSV APIs (no duplicated queries). Reports #5 and #6 (wallet balances / statement)
/// are ADMIN-only - the menu hides them for VIEWER, and the server enforces the role check.
/// </summary>
[Authorize]
public class ReportsController : Controller
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

    // ===== Report 1: Daily summary =====
    [HttpGet]
    public async Task<IActionResult> DailySummary(ReportFilterViewModel filter)
    {
        var rows = await _reports.DailySummaryAsync(filter.From, filter.To, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 2: Operators =====
    [HttpGet]
    public async Task<IActionResult> Operators(ReportFilterViewModel filter)
    {
        var rows = await _reports.OperatorsAsync(filter.From, filter.To, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 3: Partners top =====
    [HttpGet]
    public async Task<IActionResult> PartnersTop(ReportFilterViewModel filter)
    {
        var rows = await _reports.PartnersTopAsync(filter.From, filter.To, filter.Limit ?? 10, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 4: Commission =====
    [HttpGet]
    public async Task<IActionResult> Commission(ReportFilterViewModel filter)
    {
        var rows = await _reports.CommissionAsync(filter.From, filter.To, filter.PartnerId, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 5: Wallet balances as of - ADMIN ONLY =====
    [HttpGet]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Wallets(ReportFilterViewModel filter)
    {
        if (string.IsNullOrEmpty(filter.AsOf))
            filter.AsOf = DateTime.UtcNow.ToString("o");
        if (!DateTime.TryParse(filter.AsOf, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var asOfUtc))
        {
            ModelState.AddModelError("AsOf", "asOf must be an ISO date-time.");
            return View(new List<Dictionary<string, object>>());
        }
        var rows = await _reports.WalletsAsOfAsync(asOfUtc, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 6: Wallet statement - ADMIN ONLY =====
    [HttpGet]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> WalletStatement(long partnerId, ReportFilterViewModel filter)
    {
        var result = await _reports.WalletStatementAsync(partnerId, filter.From, filter.To, HttpContext.RequestAborted);
        return View(result);
    }

    // ===== Report 7: Hourly =====
    [HttpGet]
    public async Task<IActionResult> Hourly(ReportFilterViewModel filter)
    {
        if (string.IsNullOrEmpty(filter.BusinessDay))
            filter.BusinessDay = _businessDays.GetBusinessDayUtc(_time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
        var rows = await _reports.HourlyAsync(filter.BusinessDay, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 8: Failures =====
    [HttpGet]
    public async Task<IActionResult> Failures(ReportFilterViewModel filter)
    {
        var rows = await _reports.FailuresAsync(filter.From, filter.To, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 9: Reconciliation files =====
    [HttpGet]
    public async Task<IActionResult> Reconciliation(ReportFilterViewModel filter)
    {
        var rows = await _reports.ReconciliationAsync(filter.From, filter.To, HttpContext.RequestAborted);
        return View(rows);
    }

    // ===== Report 10: Partners inactive =====
    [HttpGet]
    public async Task<IActionResult> PartnersInactive(ReportFilterViewModel filter)
    {
        var rows = await _reports.PartnersInactiveAsync(filter.Days ?? 7, HttpContext.RequestAborted);
        return View(rows);
    }
}
