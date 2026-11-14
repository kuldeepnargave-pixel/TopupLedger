using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Infrastructure.Services;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public class SettlementsController : Controller
{
    private readonly SettlementService _settlementSvc;

    public SettlementsController(SettlementService settlementSvc) => _settlementSvc = settlementSvc;

    [HttpGet]
    public IActionResult Index() => View(new SettlementGenerateViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(SettlementGenerateViewModel model)
    {
        if (string.IsNullOrEmpty(model.BusinessDay))
            ModelState.AddModelError("BusinessDay", "Business day is required.");
        if (!ModelState.IsValid) return View(nameof(Index), model);

        var settlements = await _settlementSvc.GenerateForBusinessDayAsync(model.BusinessDay, HttpContext.RequestAborted);
        return View("Result", settlements);
    }
}
