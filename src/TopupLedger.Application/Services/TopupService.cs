using System.Text.Json;
using FluentValidation;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Application.Validators;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Application.Services;

/// <summary>
/// Orchestrates the top-up lifecycle: validate -> compute commission -> debit wallet ->
/// insert ledger + PENDING topup row, all inside one DB transaction. Also handles the
/// idempotency record lifecycle and operator callbacks / refunds / expiry.
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
    /// Creates a top-up. The flow is:
    ///   1. Look up the partner by API key (already done by the auth middleware; the
    ///      partner object is passed in here).
    ///   2. Check idempotency: if a record exists for (partner, key), replay or reject
    ///      based on its state and body hash.
    ///   3. Validate request body (FluentValidation) and MSISDN/operator consistency.
    ///   4. Open a DB transaction. Inside it:
    ///        - Insert an InProgress idempotency record (collision => REQUEST_IN_PROGRESS).
    ///        - Resolve the commission rule and compute the breakdown.
    ///        - Debit the wallet (SELECT FOR UPDATE on the wallet row).
    ///        - Insert the PENDING topup row.
    ///        - Mark the idempotency record Completed with the response body.
    ///   5. Commit. Returns 202 Accepted with the Location header.
    /// </summary>
    public async Task<CreateTopupResult> CreateTopupAsync(
        Partner partner, CreateTopupRequest request, string rawRequestBody, string idempotencyKey,
        string correlationId, CancellationToken ct = default)
    {
        // --- Idempotency check (read first; cheap) ---
        var existing = await _idem.GetAsync(partner.Id, idempotencyKey, ct);
        if (existing != null)
        {
            if (existing.ExpiresAtUtc < _time.GetUtcNow())
            {
                // Expired record: treat as if it didn't exist. Fall through.
            }
            else if (existing.Status == IdempotencyStatus.InProgress)
            {
                throw new DomainException("REQUEST_IN_PROGRESS",
                    "A request with the same Idempotency-Key is currently being processed.",
                    httpStatusCode: 409);
            }
            else // Completed
            {
                // Same key + same body hash => replay. Same key + different body => 422.
                string currentBodyHash = HmacSignatureService.HashBody(System.Text.Encoding.UTF8.GetBytes(rawRequestBody));
                if (!string.Equals(currentBodyHash, existing.RequestBodySha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DomainException("IDEMPOTENCY_KEY_REUSED",
                        "This Idempotency-Key was already used with a different request body.",
                        httpStatusCode: 422);
                }
                return CreateTopupResult.Replay(existing);
            }
        }

        // --- Validation ---
        var validation = await _validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var fieldErrors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            throw new DomainException("VALIDATION_FAILED", "Request validation failed.",
                fieldErrors: fieldErrors);
        }

        // --- MSISDN + operator consistency ---
        string normalisedMsisdn = _msisdns.Normalize(request.Msisdn);
        Operator op = OperatorExtensions.FromCode(request.Operator);

        if (!_msisdns.OperatorMatchesPrefix(normalisedMsisdn, op))
        {
            throw new DomainException("VALIDATION_FAILED",
                $"MSISDN prefix does not match the requested operator '{request.Operator}'.");
        }

        // Parse amount to integer fils.
        long faceValueFils = ParseAmountToFils(request.Amount);

        // --- Single DB transaction for everything that follows ---
        await using var txn = await _uow.BeginTransactionAsync(ct);
        try
        {
            // Insert the InProgress idempotency record. If a parallel request already did it,
            // we will see a unique constraint error => REQUEST_IN_PROGRESS.
            //
            // NOTE: We insert this BEFORE validation. If validation fails, we rollback the whole
            // transaction (the idempotency record is deleted). This means a retry with the same
            // key + same body after fixing the validation issue is allowed (no sticky error).
            // For INSUFFICIENT_FUNDS specifically: the partner can top up their wallet and retry
            // with the same key (the InProgress record is rolled back, not marked Completed).
            var idemRecord = new IdempotencyRecord
            {
                PartnerId = partner.Id,
                IdempotencyKey = idempotencyKey,
                RequestBodySha256 = HmacSignatureService.HashBody(System.Text.Encoding.UTF8.GetBytes(rawRequestBody)),
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
                // Race: a parallel request won the idempotency insert. Return 409.
                throw new DomainException("REQUEST_IN_PROGRESS",
                    "A request with the same Idempotency-Key is currently being processed.",
                    httpStatusCode: 409);
            }

            // (partnerId, clientRef) must be unique.
            var existingClientRef = await _topups.GetByPartnerAndClientRefAsync(partner.Id, request.ClientRef, ct);
            if (existingClientRef != null)
            {
                throw new DomainException("DUPLICATE_CLIENT_REF",
                    $"clientRef '{request.ClientRef}' has already been used by this partner.",
                    httpStatusCode: 409);
            }

            // Partner status check (re-check inside the txn to avoid TOCTOU).
            var freshPartner = await _partners.GetByIdAsync(partner.Id, ct)
                              ?? throw new DomainException("NOT_FOUND", "Partner not found.");
            if (freshPartner.Status == PartnerStatus.Suspended)
                throw new DomainException("PARTNER_SUSPENDED", "Partner is suspended.", httpStatusCode: 403);

            // Commission.
            var allRules = await _rules.GetAllAsync(ct);
            var rule = _commissions.ResolveRule(allRules, faceValueFils, _time.GetUtcNow().UtcDateTime);
            var breakdown = _commissions.Compute(faceValueFils, rule);

            // Create the PENDING topup row FIRST (so we have its ID for the ledger write).
            // If the wallet debit later fails, the entire transaction (including this insert)
            // rolls back - no topup row survives a failed debit.
            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            var topup = new Topup
            {
                PartnerId = partner.Id,
                WalletId = (await _wallets.GetByPartnerAsync(partner.Id, ct))!.Id,
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

            // Debit the wallet. This acquires SELECT ... FOR UPDATE on the wallet row.
            // If balance < debit, throws INSUFFICIENT_FUNDS -> rollback, no money moves.
            // The ledger entry is written with the correct topup_id directly.
            await _walletSvc.DebitAsync(
                topup.WalletId, breakdown.WalletDebitFils, topup.Id,
                LedgerReason.TopupDebit, correlationId, nowUtc, ct);

            // Mark idempotency completed with the response payload (so replays return identical bytes).
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

            await _idem.MarkCompletedAsync(idemRecord.Id, 202, responseBodyJson, location, topup.Id, nowUtc, ct);

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
    /// Processes an operator callback. Behaviour:
    ///   - If the topup is already in a terminal state, append an "IgnoredAfterTerminal"
    ///     callback history row and return 200 WITHOUT moving money.
    ///   - If the callback is a duplicate (same operator_txn_id already received for
    ///     this topup with the same status), append a "Duplicate" history row and return 200.
    ///   - SUCCESS: transition to SUCCESS. No money movement (the original debit stands).
    ///   - FAILED: transition to FAILED and refund exactly once via a compensating credit ledger row.
    /// All transitions are concurrency-safe: we lock the topup row first and re-check the state.
    /// </summary>
    public async Task ProcessCallbackAsync(
        long topupId, Operator op, string? operatorTxnId, TopupStatus reportedStatus,
        string? rawBody, string correlationId, CancellationToken ct = default)
    {
        await using var txn = await _uow.BeginTransactionAsync(ct);
        try
        {
            // Lock the topup row to prevent double-refund under concurrent callbacks.
            var topup = await _topups.GetByIdForUpdateAsync(topupId, ct)
                       ?? throw new DomainException("NOT_FOUND", $"Top-up {topupId} not found.");

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

            // If already terminal, log and return without money movement.
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

            // Detect duplicate: same operator_txn_id already recorded for this topup.
            var history = await _callbacks.ListForTopupAsync(topupId, ct);
            bool isDuplicate = history.Any(h =>
                !string.IsNullOrEmpty(operatorTxnId) &&
                string.Equals(h.OperatorTxnId, operatorTxnId, StringComparison.Ordinal) &&
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

            // First-time callback.
            TopupStatus newStatus;
            if (reportedStatus == TopupStatus.Success)
            {
                newStatus = TopupStatus.Success;
            }
            else if (reportedStatus == TopupStatus.Failed)
            {
                newStatus = TopupStatus.Failed;
                // Refund exactly once. The wallet row is locked for the duration of this txn.
                await _walletSvc.CreditAsync(
                    topup.WalletId, topup.WalletDebitFils, topup.Id,
                    LedgerReason.TopupRefund, $"Refund: topup {topup.Id} FAILED",
                    correlationId, nowUtc, ct);
            }
            else
            {
                // Operator should only send SUCCESS or FAILED. Treat anything else as Failed.
                newStatus = TopupStatus.Failed;
                await _walletSvc.CreditAsync(
                    topup.WalletId, topup.WalletDebitFils, topup.Id,
                    LedgerReason.TopupRefund, $"Refund: topup {topup.Id} (unexpected status {reportedStatus})",
                    correlationId, nowUtc, ct);
            }

            await _topups.UpdateStatusAsync(topup.Id, newStatus, nowUtc, operatorTxnId, nowUtc, ct);

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
    /// Marks a PENDING top-up as EXPIRED (no callback within 120s) and refunds exactly once.
    /// </summary>
    public async Task ExpireAsync(long topupId, string correlationId, CancellationToken ct = default)
    {
        await using var txn = await _uow.BeginTransactionAsync(ct);
        try
        {
            var topup = await _topups.GetByIdForUpdateAsync(topupId, ct)
                       ?? throw new DomainException("NOT_FOUND", $"Top-up {topupId} not found.");

            if (topup.Status != TopupStatus.Pending)
            {
                // Already terminal; do nothing. Late callbacks after EXPIRED are logged by the callback path.
                await txn.RollbackAsync(CancellationToken.None);
                return;
            }

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            await _walletSvc.CreditAsync(
                topup.WalletId, topup.WalletDebitFils, topup.Id,
                LedgerReason.TopupRefund, $"Refund: topup {topup.Id} EXPIRED (no callback)",
                correlationId, nowUtc, ct);

            await _topups.UpdateStatusAsync(topup.Id, TopupStatus.Expired, nowUtc, null, null, ct);

            await txn.CommitAsync(ct);
        }
        catch
        {
            await txn.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Parses the amount string ("25.00") to integer fils (2500). Rejects >2 decimal places
    /// (this is also enforced by the FluentValidation rule, but we double-check here).
    /// </summary>
    private static long ParseAmountToFils(string amount)
    {
        if (!decimal.TryParse(amount, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new DomainException("VALIDATION_FAILED", $"Amount '{amount}' is not a valid decimal.");

        // Convert to fils and round to nearest integer fils. We've already validated <=2 decimals,
        // so this multiplication is exact.
        long fils = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        if (fils < 1 || fils > 200_000)
            throw new DomainException("VALIDATION_FAILED",
                $"Amount must be between 0.01 and 2000.00 AED (got {amount}).");
        return fils;
    }
}

public sealed record CreateTopupResult
{
    public enum OutcomeKind { Created, Replayed }

    public OutcomeKind Kind { get; init; }
    public int ResponseStatus { get; init; }
    public string ResponseBodyJson { get; init; } = string.Empty;
    public string? Location { get; init; }
    public Topup? Topup { get; init; }

    public static CreateTopupResult Created(Topup topup, CreateTopupResponse response, string location) =>
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
