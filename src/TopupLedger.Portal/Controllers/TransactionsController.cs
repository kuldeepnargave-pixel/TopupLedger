using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public class TransactionsController : Controller
{
    private readonly TopupLedgerDbContext _db;
    private readonly BusinessDayService _businessDays;

    public TransactionsController(TopupLedgerDbContext db, BusinessDayService businessDays)
    {
        _db = db;
        _businessDays = businessDays;
    }

    [HttpGet]
    public async Task<IActionResult> Index(TransactionsSearchViewModel search, int page = 1, int pageSize = 25)
    {
        var q = _db.Topups.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(search.Msisdn))
        {
            string norm = new MsisdnService().Normalize(search.Msisdn);
            q = q.Where(t => t.MsisdnNormalized == norm);
        }
        if (!string.IsNullOrEmpty(search.ClientRef))
            q = q.Where(t => t.ClientRef == search.ClientRef);
        if (!string.IsNullOrEmpty(search.Status) && Enum.TryParse<TopupStatus>(search.Status, true, out var status))
            q = q.Where(t => t.Status == status);
        if (search.From.HasValue)
            q = q.Where(t => t.CreatedAtUtc >= search.From.Value);
        if (search.To.HasValue)
            q = q.Where(t => t.CreatedAtUtc <= search.To.Value);

        search.Page = page;
        search.PageSize = pageSize;
        var total = await q.LongCountAsync();
        var items = await q.OrderByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.Search = search;
        ViewBag.TotalPages = (int)Math.Ceiling((double)total / pageSize);
        ViewBag.CurrentPage = page;
        return View(items);
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        var topup = await _db.Topups.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        if (topup == null) return NotFound();
        var wallet = await _db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == topup.WalletId);
        var ledger = await _db.WalletLedger.AsNoTracking()
            .Where(l => l.TopupId == id)
            .OrderBy(l => l.Id)
            .ToListAsync();
        var callbacks = await _db.CallbackHistory.AsNoTracking()
            .Where(c => c.TopupId == id)
            .OrderBy(c => c.Id)
            .ToListAsync();

        return View(new TransactionDetailViewModel
        {
            Topup = topup,
            LedgerEntries = ledger,
            Callbacks = callbacks
        });
    }
}
