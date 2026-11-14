using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Api.Controllers;

/// <summary>
/// Partner-facing top-up endpoints. All endpoints require HMAC authentication
/// (handled by HmacAuthMiddleware on /api/v1/partner/*).
/// </summary>
[ApiController]
[Route("api/v1/partner")]
public class PartnerTopupsController : ControllerBase
{
    private readonly TopupService _topupSvc;
    private readonly ITopupRepository _topups;
    private readonly IWalletRepository _wallets;
    private readonly IPartnerRepository _partners;
    private readonly BusinessDayService _businessDays;
    private readonly TimeProvider _time;
    private readonly ILogger<PartnerTopupsController> _logger;

    public PartnerTopupsController(
        TopupService topupSvc, ITopupRepository topups, IWalletRepository wallets,
        IPartnerRepository partners, BusinessDayService businessDays,
        TimeProvider time, ILogger<PartnerTopupsController> logger)
    {
        _topupSvc = topupSvc;
        _topups = topups;
        _wallets = wallets;
        _partners = partners;
        _businessDays = businessDays;
        _time = time;
        _logger = logger;
    }

    /// <summary>POST /api/v1/partner/topups - Create a top-up. 202 Accepted with Location header.</summary>
    [HttpPost("topups")]
    [ProducesResponseType(typeof(CreateTopupResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateTopup([FromBody] CreateTopupRequest request)
    {
        var partner = HttpContext.Items["Partner"] as Partner
                     ?? throw new DomainException("AUTH_INVALID_SIGNATURE", "Authentication required.", httpStatusCode: 401);
        var rawBody = HttpContext.Items["RawRequestBody"] as byte[] ?? Array.Empty<byte>();
        var idemKey = HttpContext.Request.Headers["Idempotency-Key"].ToString();
        var correlationId = HttpContext.Items["CorrelationId"] as string ?? HttpContext.TraceIdentifier;

        var rawJson = System.Text.Encoding.UTF8.GetString(rawBody);
        var result = await _topupSvc.CreateTopupAsync(partner, request, rawJson, idemKey, correlationId, HttpContext.RequestAborted);

        if (result.Kind == CreateTopupResult.OutcomeKind.Replayed)
        {
            // Idempotent replay: return the original status code and body.
            Response.StatusCode = result.ResponseStatus;
            Response.ContentType = "application/json";
            await Response.WriteAsync(result.ResponseBodyJson, HttpContext.RequestAborted);
            return new EmptyResult();
        }

        // Append the correlation id to the response headers.
        Response.Headers["Location"] = result.Location;
        Response.Headers["X-Correlation-Id"] = correlationId;
        return StatusCode(StatusCodes.Status202Accepted, result.Topup == null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(result.ResponseBodyJson));
    }

    /// <summary>GET /api/v1/partner/topups/{id} - Fetch a single top-up. 404 for other partners' topups.</summary>
    [HttpGet("topups/{id:long}")]
    public async Task<IActionResult> GetTopup(long id)
    {
        var partner = HttpContext.Items["Partner"] as Partner
                     ?? throw new DomainException("AUTH_INVALID_SIGNATURE", "Authentication required.", httpStatusCode: 401);
        var topup = await _topups.GetByIdAsync(id, HttpContext.RequestAborted);
        if (topup == null || topup.PartnerId != partner.Id)
            throw new DomainException("NOT_FOUND", $"Top-up {id} not found.", httpStatusCode: 404);

        return Ok(MapDetail(topup));
    }

    /// <summary>GET /api/v1/partner/topups - Keyset pagination. No OFFSET.</summary>
    [HttpGet("topups")]
    public async Task<IActionResult> ListTopups(
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        var partner = HttpContext.Items["Partner"] as Partner
                     ?? throw new DomainException("AUTH_INVALID_SIGNATURE", "Authentication required.", httpStatusCode: 401);

        if (limit < 1 || limit > 200) limit = 50;
        var statusEnum = ParseStatus(status);

        var (items, nextCursor) = await _topups.ListAsync(
            partner.Id, statusEnum, from, to, limit, cursor, HttpContext.RequestAborted);

        var response = new ListTopupsResponse
        {
            Items = items.Select(t => new TopupListItem
            {
                Id = t.Id,
                Status = t.Status.ToString().ToUpperInvariant(),
                MsisdnMasked = t.MsisdnMasked,
                Operator = t.Operator.ToCode().ToUpperInvariant(),
                ClientRef = t.ClientRef,
                FaceValueFils = t.FaceValueFils,
                FaceValueAed = MoneyFormatter.ToAed(t.FaceValueFils),
                CreatedAtUtc = t.CreatedAtUtc
            }).ToList(),
            NextCursor = nextCursor
        };
        return Ok(response);
    }

    /// <summary>GET /api/v1/partner/wallet - Wallet balance + last 20 ledger entries.</summary>
    [HttpGet("wallet")]
    public async Task<IActionResult> GetWallet()
    {
        var partner = HttpContext.Items["Partner"] as Partner
                     ?? throw new DomainException("AUTH_INVALID_SIGNATURE", "Authentication required.", httpStatusCode: 401);
        var wallet = await _wallets.GetByPartnerAsync(partner.Id, HttpContext.RequestAborted);
        if (wallet == null) throw new DomainException("NOT_FOUND", "Wallet not found.", httpStatusCode: 404);
        var entries = await _wallets.GetRecentEntriesAsync(wallet.Id, 20, HttpContext.RequestAborted);

        return Ok(new WalletResponse
        {
            WalletId = wallet.Id,
            PartnerId = partner.Id,
            BalanceFils = wallet.BalanceFils,
            BalanceAed = MoneyFormatter.ToAed(wallet.BalanceFils),
            Currency = wallet.Currency,
            RecentEntries = entries.Select(e => new LedgerEntryDto
            {
                Id = e.Id,
                EntryType = e.EntryType.ToString().ToUpperInvariant(),
                Reason = e.Reason.ToString(),
                AmountFils = e.AmountFils,
                AmountAed = MoneyFormatter.ToAed(e.AmountFils),
                RunningBalanceFils = e.RunningBalanceFils,
                RunningBalanceAed = MoneyFormatter.ToAed(e.RunningBalanceFils),
                ReasonText = e.ReasonText,
                CreatedAtUtc = e.CreatedAtUtc
            }).ToList()
        });
    }

    /// <summary>GET /api/v1/partner/settlements/{businessDay} - Settlement for one business day.</summary>
    [HttpGet("settlements/{businessDay}")]
    public async Task<IActionResult> GetSettlement(string businessDay,
        [FromServices] TopupLedger.Infrastructure.Services.SettlementService settlements)
    {
        var partner = HttpContext.Items["Partner"] as Partner
                     ?? throw new DomainException("AUTH_INVALID_SIGNATURE", "Authentication required.", httpStatusCode: 401);
        var settlement = await settlements.GetAsync(partner.Id, businessDay, HttpContext.RequestAborted);
        return Ok(settlement);
    }

    private static Domain.Enums.TopupStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        return status.ToUpperInvariant() switch
        {
            "PENDING" => Domain.Enums.TopupStatus.Pending,
            "SUCCESS" => Domain.Enums.TopupStatus.Success,
            "FAILED" => Domain.Enums.TopupStatus.Failed,
            "EXPIRED" => Domain.Enums.TopupStatus.Expired,
            _ => throw new DomainException("VALIDATION_FAILED", $"Invalid status '{status}'. Allowed: PENDING, SUCCESS, FAILED, EXPIRED.", httpStatusCode: 400)
        };
    }

    private static TopupDetailResponse MapDetail(Topup t) => new()
    {
        Id = t.Id,
        PartnerId = t.PartnerId,
        PartnerName = string.Empty,
        Status = t.Status.ToString().ToUpperInvariant(),
        MsisdnMasked = t.MsisdnMasked,
        Operator = t.Operator.ToCode().ToUpperInvariant(),
        ClientRef = t.ClientRef,
        FaceValueFils = t.FaceValueFils,
        FaceValueAed = MoneyFormatter.ToAed(t.FaceValueFils),
        RetailerCommissionFils = t.RetailerCommissionFils,
        RetailerCommissionAed = MoneyFormatter.ToAed(t.RetailerCommissionFils),
        DistributorShareFils = t.DistributorShareFils,
        DistributorShareAed = MoneyFormatter.ToAed(t.DistributorShareFils),
        WalletDebitFils = t.WalletDebitFils,
        WalletDebitAed = MoneyFormatter.ToAed(t.WalletDebitFils),
        OperatorTxnId = t.OperatorTxnId,
        CreatedAtUtc = t.CreatedAtUtc,
        BusinessDate = t.BusinessDateUtc,
        FinalStateAtUtc = t.FinalStateAtUtc,
        CallbackReceivedAtUtc = t.CallbackReceivedAtUtc
    };
}
