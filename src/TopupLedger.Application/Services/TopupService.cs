
using System.Text.Json;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Validators;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Application.Services;

/// <summary>
/// Orchestrates the top-up lifecycle: validate -> compute commission -> debit wallet ->
/// insert ledger + PENDING topup row, all inside one DB transaction.
/// Also handles idempotency, operator callbacks, refunds and expiry.
/// </summary>
public sealed class TopupService
{
    private readonly IUnitOfWork _uow;
    private readonly IPartnerRepository _partners;
    private readonly IWalletRepository _wallets;
    private readonly ITopupRepository _topups;
    private readonly ICommissionRuleRepository _rules;
    private readonly IIdempotencyRepository _idem;
    private readonly ICallbackHistoryRepository _callbacks;
    private readonly CommissionService _commissions;
    private readonly MsisdnService _msisdns;
    private readonly BusinessDayService _businessDays;
    private readonly WalletService _walletSvc;
    private readonly CreateTopupRequestValidator _validator;
    private readonly TimeProvider _time;

    public TopupService(
        IUnitOfWork uow,
        IPartnerRepository partners,
        IWalletRepository wallets,
        ITopupRepository topups,
        ICommissionRuleRepository rules,
        IIdempotencyRepository idem,
        ICallbackHistoryRepository callbacks,
        CommissionService commissions,
        MsisdnService msisdns,
        BusinessDayService businessDays,
        WalletService walletSvc,
        CreateTopupRequestValidator validator,
        TimeProvider? time = null)
    {
        _uow = uow;
        _partners = partners;
        _wallets = wallets;
        _topups = topups;
        _rules = rules;
        _idem = idem;
        _callbacks = callbacks;
        _commissions = commissions;
        _msisdns = msisdns;
        _businessDays = businessDays;
        _walletSvc = walletSvc;
        _validator = validator;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a top-up with idempotency protection and atomic wallet debit.
    /// </summary>
    public async Task<CreateTopupResult> CreateTopupAsync(
        Partner partner,
        CreateTopupRequest request,
        string rawRequestBody,
        string idempotencyKey,
        string correlationId,
        CancellationToken ct = default)
    {
        // Idempotency check.
        var existing = await _idem.GetAsync(partner.Id, idempotencyKey, ct);

        if (existing != null)
        {
            if (existing.ExpiresAtUtc < _time.GetUtcNow())
            {
                // Expired record: allow a new request.
            }
            else if (existing.Status == IdempotencyStatus.InProgress)
            {
                throw new DomainException(
                    "REQUEST_IN_PROGRESS",
                    "A request with the same Idempotency-Key is currently being processed.",
                    httpStatusCode: 409);
            }
            else
            {
                string currentBodyHash = HmacSignatureService.HashBody(
                    System.Text.Encoding.UTF8.GetBytes(rawRequestBody));

                if (!string.Equals(
                    currentBodyHash,
                    existing.RequestBodySha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new DomainException(
                        "IDEMPOTENCY_KEY_REUSED",
                        "This Idempotency-Key was already used with a different request body.",
                        httpStatusCode: 422);
                }

                return CreateTopupResult.Replay(existing);
            }
        }

        // Request validation.
        var validation = await _validator.ValidateAsync(request, ct);

        if (!validation.IsValid)
        {
            var fieldErrors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            throw new DomainException(
                "VALIDATION_FAILED",
                "Request validation failed.",
                fieldErrors: fieldErrors);
        }

        // MSISDN and operator validation.
        string normalisedMsisdn = _msisdns.Normalize(request.Msisdn);
        Operator op = OperatorExtensions.FromCode(request.Operator);

        if (!_msisdns.OperatorMatchesPrefix(normalisedMsisdn, op))
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                $"MSISDN prefix does not match the requested operator '{request.Operator}'.");
        }

        long faceValueFils = ParseAmountToFils(request.Amount);

        // Begin transaction.
        await using var txn = await _uow.BeginTransactionAsync(ct);

        try
        {
            var idemRecord = new IdempotencyRecord
            {
                PartnerId = partner.Id,
                IdempotencyKey = idempotencyKey,
                RequestBodySha256 = HmacSignatureService.HashBody(
                    System.Text.Encoding.UTF8.GetBytes(rawRequestBody)),
                ClientRef = request.ClientRef,
                Status = IdempotencyStatus.InProgress,
                CreatedAtUtc = _time.GetUtcNow().UtcDateTime,
                ExpiresAtUtc = _time.GetUtcNow().UtcDateTime.AddHours(24)
            };

            try
            {
                await _idem.InsertInProgressAsync(idemRecord, ct);
            }
            catch (UniqueConstraintException)
            {
                throw new DomainException(
                    "REQUEST_IN_PROGRESS",
                    "A request with the same Idempotency-Key is currently being processed.",
                    httpStatusCode: 409);
            }

            // Check duplicate client reference.
            var existingClientRef =
                await _topups.GetByPartnerAndClientRefAsync(
                    partner.Id,
                    request.ClientRef,
                    ct);

            if (existingClientRef != null)
            {
                throw new DomainException(
                    "DUPLICATE_CLIENT_REF",
                    $"clientRef '{request.ClientRef}' has already been used by this partner.",
                    httpStatusCode: 409);
            }

            // Check partner status.
            var freshPartner = await _partners.GetByIdAsync(partner.Id, ct)
                ?? throw new DomainException("NOT_FOUND", "Partner not found.");

            if (freshPartner.Status == PartnerStatus.Suspended)
            {
                throw new DomainException(
                    "PARTNER_SUSPENDED",
                    "Partner is suspended.",
                    httpStatusCode: 403);
            }

            // Calculate commission.
            var allRules = await _rules.GetAllAsync(ct);

            var rule = _commissions.ResolveRule(
                allRules,
                faceValueFils,
                _time.GetUtcNow().UtcDateTime);

            var breakdown = _commissions.Compute(faceValueFils, rule);

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

            // Get wallet using PartnerId.
            var wallet = await _wallets.GetByPartnerAsync(partner.Id, ct)
                ?? throw new DomainException(
                    "NOT_FOUND",
                    $"Wallet not found for partner {partner.Id}.");

            // Create pending top-up with actual WalletId.
            var topup = new Topup
            {
                PartnerId = partner.Id,
                WalletId = wallet.Id,
                ClientRef = request.ClientRef,
                MsisdnNormalized = normalisedMsisdn,
                MsisdnMasked = MsisdnService.MaskForDisplay(normalisedMsisdn),
                Operator = op,
                FaceValueFils = faceValueFils,
                RetailerCommissionFils = breakdown.RetailerCommissionFils,
                DistributorShareFils = breakdown.DistributorShareFils,
                WalletDebitFils = breakdown.WalletDebitFils,
                Status = TopupStatus.Pending,
                CreatedAtUtc = nowUtc,
                BusinessDateUtc = _businessDays.GetBusinessDayUtc(nowUtc)
            };

            topup.Id = await _topups.CreateAsync(topup, ct);

            // Debit wallet using PartnerId.
            await _walletSvc.DebitAsync(
                partner.Id,
                breakdown.WalletDebitFils,
                topup.Id,
                LedgerReason.TopupDebit,
                correlationId,
                nowUtc,
                ct);

            // Prepare response.
            var response = new CreateTopupResponse
            {
                Id = topup.Id,
                Status = "PENDING",
                Msisdn = topup.MsisdnMasked,
                FaceValueFils = topup.FaceValueFils,
                FaceValueAed = MoneyFormatter.ToAed(topup.FaceValueFils),
                RetailerCommissionFils = topup.RetailerCommissionFils,
                RetailerCommissionAed = MoneyFormatter.ToAed(topup.RetailerCommissionFils),
                DistributorShareFils = topup.DistributorShareFils,
                DistributorShareAed = MoneyFormatter.ToAed(topup.DistributorShareFils),
                WalletDebitFils = topup.WalletDebitFils,
                WalletDebitAed = MoneyFormatter.ToAed(topup.WalletDebitFils),
                Operator = topup.Operator.ToCode().ToUpperInvariant(),
                ClientRef = topup.ClientRef,
                CreatedAtUtc = topup.CreatedAtUtc,
                BusinessDate = topup.BusinessDateUtc
            };

            string responseBodyJson = JsonSerializer.Serialize(response);
            string location = $"/api/v1/partner/topups/{topup.Id}";

            await _idem.MarkCompletedAsync(
                idemRecord.Id,
                202,
                responseBodyJson,
                location,
                topup.Id,
                nowUtc,
                ct);

            await txn.CommitAsync(ct);

            return CreateTopupResult.Created(topup, response, location);
        }
        catch
        {
            await txn.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Processes operator callbacks and refunds failed top-ups exactly once.
    /// </summary>
    public async Task ProcessCallbackAsync(
        long topupId,
        Operator op,
        string? operatorTxnId,
        TopupStatus reportedStatus,
        string? rawBody,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var txn = await _uow.BeginTransactionAsync(ct);

        try
        {
            var topup = await _topups.GetByIdForUpdateAsync(topupId, ct)
                ?? throw new DomainException(
                    "NOT_FOUND",
                    $"Top-up {topupId} not found.");

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

            if (topup.Status != TopupStatus.Pending)
            {
                await _callbacks.AppendAsync(new CallbackHistory
                {
                    TopupId = topupId,
                    Operator = op,
                    OperatorTxnId = operatorTxnId,
                    ReportedStatus = reportedStatus,
                    Outcome = CallbackStatus.IgnoredAfterTerminal,
                    ReceivedAtUtc = nowUtc,
                    RawBody = rawBody
                }, ct);

                await txn.CommitAsync(ct);
                return;
            }

            var history = await _callbacks.ListForTopupAsync(topupId, ct);

            bool isDuplicate = history.Any(h =>
                !string.IsNullOrEmpty(operatorTxnId) &&
                string.Equals(
                    h.OperatorTxnId,
                    operatorTxnId,
                    StringComparison.Ordinal) &&
                h.ReportedStatus == reportedStatus &&
                h.Outcome == CallbackStatus.Received);

            if (isDuplicate)
            {
                await _callbacks.AppendAsync(new CallbackHistory
                {
                    TopupId = topupId,
                    Operator = op,
                    OperatorTxnId = operatorTxnId,
                    ReportedStatus = reportedStatus,
                    Outcome = CallbackStatus.Duplicate,
                    ReceivedAtUtc = nowUtc,
                    RawBody = rawBody
                }, ct);

                await txn.CommitAsync(ct);
                return;
            }

            TopupStatus newStatus;

            if (reportedStatus == TopupStatus.Success)
            {
                newStatus = TopupStatus.Success;
            }
            else if (reportedStatus == TopupStatus.Failed)
            {
                newStatus = TopupStatus.Failed;

                // Refund using PartnerId, not WalletId.
                await _walletSvc.CreditAsync(
                    topup.PartnerId,
                    topup.WalletDebitFils,
                    topup.Id,
                    LedgerReason.TopupRefund,
                    $"Refund: topup {topup.Id} FAILED",
                    correlationId,
                    nowUtc,
                    ct);
            }
            else
            {
                newStatus = TopupStatus.Failed;

                await _walletSvc.CreditAsync(
                    topup.PartnerId,
                    topup.WalletDebitFils,
                    topup.Id,
                    LedgerReason.TopupRefund,
                    $"Refund: topup {topup.Id} (unexpected status {reportedStatus})",
                    correlationId,
                    nowUtc,
                    ct);
            }

            await _topups.UpdateStatusAsync(
                topup.Id,
                newStatus,
                nowUtc,
                operatorTxnId,
                nowUtc,
                ct);

            await _callbacks.AppendAsync(new CallbackHistory
            {
                TopupId = topupId,
                Operator = op,
                OperatorTxnId = operatorTxnId,
                ReportedStatus = reportedStatus,
                Outcome = CallbackStatus.Received,
                ReceivedAtUtc = nowUtc,
                RawBody = rawBody
            }, ct);

            await txn.CommitAsync(ct);
        }
        catch
        {
            await txn.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Marks a pending top-up as expired and refunds the wallet.
    /// </summary>
    public async Task ExpireAsync(
        long topupId,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var txn = await _uow.BeginTransactionAsync(ct);

        try
        {
            var topup = await _topups.GetByIdForUpdateAsync(topupId, ct)
                ?? throw new DomainException(
                    "NOT_FOUND",
                    $"Top-up {topupId} not found.");

            if (topup.Status != TopupStatus.Pending)
            {
                await txn.RollbackAsync(CancellationToken.None);
                return;
            }

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

            await _walletSvc.CreditAsync(
                topup.PartnerId,
                topup.WalletDebitFils,
                topup.Id,
                LedgerReason.TopupRefund,
                $"Refund: topup {topup.Id} EXPIRED (no callback)",
                correlationId,
                nowUtc,
                ct);

            await _topups.UpdateStatusAsync(
                topup.Id,
                TopupStatus.Expired,
                nowUtc,
                null,
                null,
                ct);

            await txn.CommitAsync(ct);
        }
        catch
        {
            await txn.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Parses amount string to integer fils.
    /// </summary>
    private static long ParseAmountToFils(string amount)
    {
        if (!decimal.TryParse(
            amount,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value))
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                $"Amount '{amount}' is not a valid decimal.");
        }

        long fils = (long)Math.Round(
            value * 100m,
            MidpointRounding.AwayFromZero);

        if (fils < 1 || fils > 200_000)
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                $"Amount must be between 0.01 and 2000.00 AED (got {amount}).");
        }

        return fils;
    }
}

public sealed record CreateTopupResult
{
    public enum OutcomeKind
    {
        Created,
        Replayed
    }

    public OutcomeKind Kind { get; init; }
    public int ResponseStatus { get; init; }
    public string ResponseBodyJson { get; init; } = string.Empty;
    public string? Location { get; init; }
    public Topup? Topup { get; init; }

    public static CreateTopupResult Created(
        Topup topup,
        CreateTopupResponse response,
        string location) =>
        new()
        {
            Kind = OutcomeKind.Created,
            ResponseStatus = 202,
            ResponseBodyJson = JsonSerializer.Serialize(response),
            Location = location,
            Topup = topup
        };

    public static CreateTopupResult Replay(IdempotencyRecord record) =>
        new()
        {
            Kind = OutcomeKind.Replayed,
            ResponseStatus = record.ResponseStatus,
            ResponseBodyJson = record.ResponseBodyJson ?? "{}",
            Location = record.ResourceLocation,
            Topup = null
        };
}
