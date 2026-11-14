using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Api.Controllers;

/// <summary>
/// Operator status callbacks. Authenticated by X-Operator-Token (OperatorCallbackAuthMiddleware).
/// Body: { "operatorTxnId", "topupId", "status" }. May be DUPLICATED - duplicates return 200
/// and change nothing. Late callbacks after terminal state return 200 and do not move money.
/// </summary>
[ApiController]
[Route("api/v1/operator/callbacks/{operator}")]
public class OperatorCallbacksController : ControllerBase
{
    private readonly TopupService _topupSvc;
    private readonly ILogger<OperatorCallbacksController> _logger;
    private readonly TopupLedgerDbContext _db;

    public OperatorCallbacksController(TopupService topupSvc, ILogger<OperatorCallbacksController> logger, TopupLedgerDbContext db)
    {
        _topupSvc = topupSvc;
        _logger = logger;
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> Callback(string @operator, [FromBody] OperatorCallbackRequest body)
    {
        if (!Enum.TryParse<TopupStatus>(body.Status, true, out var reportedStatus)
            || (reportedStatus != TopupStatus.Success && reportedStatus != TopupStatus.Failed))
        {
            throw new DomainException("VALIDATION_FAILED", "status must be 'SUCCESS' or 'FAILED'.", httpStatusCode: 400);
        }

        Operator op;
        try { op = OperatorExtensions.FromCode(@operator); }
        catch { throw new DomainException("VALIDATION_FAILED", $"Unknown operator '{@operator}'.", httpStatusCode: 400); }

        var correlationId = HttpContext.TraceIdentifier;
        var rawBody = HttpContext.Items["RawRequestBody"] as byte[];
        string? rawJson = rawBody != null ? System.Text.Encoding.UTF8.GetString(rawBody) : null;

        // Mask any MSISDN if present in the raw body before persisting it.
        // (We don't expect MSISDN in callback bodies, but the rule applies to all stored bodies.)
        await _topupSvc.ProcessCallbackAsync(body.TopupId, op, body.OperatorTxnId, reportedStatus, rawJson, correlationId, HttpContext.RequestAborted);

        _logger.LogInformation("Operator callback processed: topup={id} operator={op} status={st} corr={corr}",
            body.TopupId, @operator, body.Status, correlationId);
        return Ok(new { status = "processed" });
    }
}
