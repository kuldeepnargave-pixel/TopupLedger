using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Services;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public class ReconciliationController : Controller
{
    private readonly ReconciliationService _reconSvc;
    private readonly ILogger<ReconciliationController> _logger;

    public ReconciliationController(ReconciliationService reconSvc, ILogger<ReconciliationController> logger)
    {
        _reconSvc = reconSvc;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View(new ReconciliationUploadViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50_000_000)]  // 50 MB
    public async Task<IActionResult> Upload(ReconciliationUploadViewModel model)
    {
        if (model.File == null || model.File.Length == 0)
        {
            ModelState.AddModelError("File", "Please upload a file.");
            return View(nameof(Index), model);
        }

        var op = OperatorExtensions.FromCode(model.Operator);
        using var stream = model.File.OpenReadStream();
        var result = await _reconSvc.ProcessAsync(model.File.FileName, stream, op, HttpContext.RequestAborted);

        TempData["Result"] = $"Processed {result.TotalRows} rows: matched={result.MatchedCount}, missing-in-ours={result.MissingInOursCount}, missing-in-operator={result.MissingInOperatorCount}, amount-mismatch={result.AmountMismatchCount}, status-mismatch={result.StatusMismatchCount}. Already existed: {result.AlreadyExisted}.";
        return RedirectToAction(nameof(Index));
    }
}
