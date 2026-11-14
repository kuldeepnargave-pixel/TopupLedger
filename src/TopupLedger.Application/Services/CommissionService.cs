using System.Globalization;
using TopupLedger.Application.Exceptions;
using TopupLedger.Domain.Entities;

namespace TopupLedger.Application.Services;

/// <summary>
/// Commission calculation. All money is integer fils. Percentages use decimal internally
/// (only for the multiplication step) and the final result is rounded to a long using
/// half-up rounding. Float/double never touch money.
///
/// Rules from the assignment (Section 4):
///   retailerCommission = round_half_up(faceValueFils * retailerRate / 100)
///   distributorShare    = round_half_up(faceValueFils * distributorRate / 100) - retailerCommission
///   walletDebit         = faceValueFils - retailerCommission
///
/// Effective dates are interpreted in Dubai local time ("from" inclusive, "to" exclusive).
/// </summary>
public sealed class CommissionService
{
    private readonly TimeProvider _timeProvider;

    public CommissionService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Half-up rounding: 0.5 and above rounds up (e.g. 274.5 -> 275, 274.4 -> 274).
    /// Implemented via Math.Round with MidpointRounding.AwayFromZero on a decimal value.
    /// </summary>
    public static long RoundHalfUp(decimal value) =>
        (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Returns the commission rule applicable to (faceValueFils, transactionTimeUtc).
    /// Throws if face value is outside the supported range [1, 200000] fils.
    /// </summary>
    public CommissionRule ResolveRule(IEnumerable<CommissionRule> rules, long faceValueFils, DateTime transactionTimeUtc)
    {
        if (faceValueFils < 1 || faceValueFils > 200_000)
            throw new DomainException(
                "VALIDATION_FAILED",
                $"Face value {faceValueFils} fils is outside the allowed range 1..200000 (0.01..2000.00 AED).");

        // Pick the most-specific rule whose face-value range contains the value and
        // whose effective window contains the transaction instant.
        var applicable = rules
            .Where(r => faceValueFils >= r.FaceValueFromFils && faceValueFils <= r.FaceValueToFils)
            .Where(r => transactionTimeUtc >= r.EffectiveFromUtc)
            .Where(r => r.EffectiveToUtc == null || transactionTimeUtc < r.EffectiveToUtc.Value)
            .OrderByDescending(r => r.EffectiveFromUtc)   // newest applicable rule wins
            .FirstOrDefault();

        if (applicable == null)
            throw new DomainException("VALIDATION_FAILED",
                $"No commission rule applicable for face value {faceValueFils} fils at {transactionTimeUtc:O}.");

        return applicable;
    }

    /// <summary>
    /// Computes retailer commission, distributor share and wallet debit.
    /// All values are returned as integer fils.
    /// </summary>
    public CommissionBreakdown Compute(long faceValueFils, CommissionRule rule)
    {
        // Use decimal for the multiplication to keep precision, then round half-up
        // to a long. Distributor share is computed as the rounded distributor commission
        // MINUS the (already rounded) retailer commission, as required by the assignment.
        decimal retailerPct = rule.RetailerRatePct;
        decimal distributorPct = rule.DistributorRatePct;

        long retailerCommission = RoundHalfUp(faceValueFils * retailerPct / 100m);
        long distributorCommissionFull = RoundHalfUp(faceValueFils * distributorPct / 100m);
        long distributorShare = distributorCommissionFull - retailerCommission;
        long walletDebit = faceValueFils - retailerCommission;

        return new CommissionBreakdown(
            faceValueFils,
            retailerCommission,
            distributorShare,
            distributorCommissionFull,
            walletDebit,
            rule.Id);
    }
}

public readonly record struct CommissionBreakdown(
    long FaceValueFils,
    long RetailerCommissionFils,
    long DistributorShareFils,
    long DistributorCommissionFullFils,
    long WalletDebitFils,
    long CommissionRuleId);
