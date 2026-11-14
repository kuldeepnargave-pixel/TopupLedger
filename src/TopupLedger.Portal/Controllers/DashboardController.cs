using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Services;
using TopupLedger.Infrastructure.Data;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly TopupLedgerDbContext _db;
    private readonly BusinessDayService _businessDays;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(TopupLedgerDbContext db, BusinessDayService businessDays, ILogger<DashboardController> logger)
    {
        _db = db;
        _businessDays = businessDays;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var today = _businessDays.GetBusinessDayUtc(DateTime.UtcNow);
        var topups = _db.Topups.AsNoTracking().Where(t => t.BusinessDateUtc == today);

        var statusCounts = await topups.GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Status.ToString().ToUpperInvariant(), x => x.Count);

        var operatorCounts = await topups.GroupBy(t => t.Operator)
            .Select(g => new { Operator = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Operator.ToString().ToUpperInvariant(), x => x.Count);

        long totalFaceValue = await topups.SumAsync(t => t.FaceValueFils);

        return View(new DashboardViewModel
        {
            BusinessDay = today,
            TotalCount = statusCounts.Values.Sum(),
            TotalFaceValueFils = totalFaceValue,
            StatusCounts = statusCounts,
            OperatorCounts = operatorCounts
        });
    }
}
