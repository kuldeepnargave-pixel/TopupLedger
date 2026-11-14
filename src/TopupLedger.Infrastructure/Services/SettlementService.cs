using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Infrastructure.Services;

/// <summary>
/// Generates end-of-business-day settlements. The settlement row aggregates SUCCESS, FAILED,
/// EXPIRED and PENDING counts + total face value + commissions for one (partner, businessDay).
///
/// Re-generating a settlement for the same (partner, businessDay) is idempotent: a unique index
/// on (partner_id, business_date_utc) prevents duplicate settlements. If the settlement already
/// exists, the existing one is returned untouched.
/// </summary>
public sealed class SettlementService
{
    private readonly ISettlementRepository _settlements;
    private readonly IPartnerRepository _partners;
    private readonly TopupLedgerDbContext _db;
    private readonly BusinessDayService _businessDays;
    private readonly ILogger<SettlementService> _logger;

    public SettlementService(
        ISettlementRepository settlements,
        IPartnerRepository partners,
        TopupLedgerDbContext db,
        BusinessDayService businessDays,
        ILogger<SettlementService> logger)
    {
        _settlements = settlements;
        _partners = partners;
        _db = db;
        _businessDays = businessDays;
        _logger = logger;
    }

    /// <summary>
    /// Generates the settlement for ALL active partners for the given business day. Already-
    /// generated settlements are skipped (idempotent). Returns the list of settlements (old + new).
    /// </summary>
    public async Task<IReadOnlyList<SettlementResponse>> GenerateForBusinessDayAsync(string businessDayYyyyMmDd, CancellationToken ct = default)
    {
        var businessDayUtc = _businessDays.ParseBusinessDay(businessDayYyyyMmDd);
        var nextDayUtc = businessDayUtc.AddDays(1);

        var partners = await _db.Partners.AsNoTracking().ToListAsync(ct);
        var results = new List<SettlementResponse>();

        foreach (var partner in partners)
        {
            var existing = await _settlements.GetAsync(partner.Id, businessDayUtc, ct);
            if (existing != null)
            {
                results.Add(MapResponse(partner.Name, existing));
                continue;
            }

            // Aggregate top-ups for this partner on this business day.
            var topups = await _db.Topups.AsNoTracking()
                .Where(t => t.PartnerId == partner.Id && t.BusinessDateUtc == businessDayUtc)
                .ToListAsync(ct);

            var success = topups.Where(t => t.Status == TopupStatus.Success);
            var settlement = new Settlement
            {
                PartnerId = partner.Id,
                BusinessDateUtc = businessDayUtc,
                SuccessCount = topups.Count(t => t.Status == TopupStatus.Success),
                FailedCount = topups.Count(t => t.Status == TopupStatus.Failed),
                ExpiredCount = topups.Count(t => t.Status == TopupStatus.Expired),
                PendingCount = topups.Count(t => t.Status == TopupStatus.Pending),
                TotalFaceValueFils = success.Sum(t => t.FaceValueFils),
                TotalRetailerCommissionFils = success.Sum(t => t.RetailerCommissionFils),
                TotalDistributorShareFils = success.Sum(t => t.DistributorShareFils),
                NetDebitFils = success.Sum(t => t.WalletDebitFils),
                GeneratedAtUtc = DateTime.UtcNow
            };

            var items = topups.Select(t => new SettlementItem
            {
                TopupId = t.Id,
                FaceValueFils = t.FaceValueFils,
                RetailerCommissionFils = t.RetailerCommissionFils,
                DistributorShareFils = t.DistributorShareFils,
                Status = t.Status,
                Operator = t.Operator
            }).ToList();

            await _settlements.CreateAsync(settlement, items, ct);
            results.Add(MapResponse(partner.Name, settlement));
        }

        return results;
    }

    /// <summary>
    /// Returns the settlement for a specific (partner, businessDay). Throws NOT_FOUND if not generated.
    /// </summary>
    public async Task<SettlementResponse> GetAsync(long partnerId, string businessDayYyyyMmDd, CancellationToken ct = default)
    {
        var businessDayUtc = _businessDays.ParseBusinessDay(businessDayYyyyMmDd);
        var s = await _settlements.GetAsync(partnerId, businessDayUtc, ct)
            ?? throw new DomainException("NOT_FOUND",
                $"No settlement generated for partner {partnerId} on business day {businessDayYyyyMmDd}.", httpStatusCode: 404);
        var partner = await _partners.GetByIdAsync(partnerId, ct);
        return MapResponse(partner?.Name ?? "(unknown)", s);
    }

    public async Task<IReadOnlyList<SettlementResponse>> ListForDayAsync(string businessDayYyyyMmDd, CancellationToken ct = default)
    {
        var businessDayUtc = _businessDays.ParseBusinessDay(businessDayYyyyMmDd);
        var settlements = await _settlements.ListForDayAsync(businessDayUtc, ct);
        var partners = (await _db.Partners.AsNoTracking().ToListAsync(ct)).ToDictionary(p => p.Id);
        return settlements.Select(s => MapResponse(partners.GetValueOrDefault(s.PartnerId)?.Name ?? "(unknown)", s)).ToList();
    }

    private static SettlementResponse MapResponse(string partnerName, Settlement s) => new()
    {
        PartnerId = s.PartnerId,
        PartnerName = partnerName,
        BusinessDate = s.BusinessDateUtc,
        SuccessCount = s.SuccessCount,
        FailedCount = s.FailedCount,
        ExpiredCount = s.ExpiredCount,
        PendingCount = s.PendingCount,
        TotalFaceValueFils = s.TotalFaceValueFils,
        TotalFaceValueAed = MoneyFormatter.ToAed(s.TotalFaceValueFils),
        TotalRetailerCommissionFils = s.TotalRetailerCommissionFils,
        TotalRetailerCommissionAed = MoneyFormatter.ToAed(s.TotalRetailerCommissionFils),
        TotalDistributorShareFils = s.TotalDistributorShareFils,
        TotalDistributorShareAed = MoneyFormatter.ToAed(s.TotalDistributorShareFils),
        NetDebitFils = s.NetDebitFils,
        NetDebitAed = MoneyFormatter.ToAed(s.NetDebitFils),
        GeneratedAtUtc = s.GeneratedAtUtc
    };
}
