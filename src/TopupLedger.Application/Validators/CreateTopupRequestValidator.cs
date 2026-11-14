using FluentValidation;
using TopupLedger.Application.DTOs;

namespace TopupLedger.Application.Validators;

/// <summary>
/// Validates a top-up request before any money moves. The validation rules enforce
/// the amount range [0.01, 2000.00] AED, max 2 decimal places, MSISDN presence and operator
/// format. The MSISDN/operator match against prefix is checked separately by MsisdnService
/// (after normalisation).
/// </summary>
public sealed class CreateTopupRequestValidator : AbstractValidator<CreateTopupRequest>
{
    public CreateTopupRequestValidator()
    {
        RuleFor(x => x.Msisdn)
            .NotEmpty().WithMessage("MSISDN is required.");

        RuleFor(x => x.Amount)
            .NotEmpty().WithMessage("Amount is required.")
            .Must(BeValidAmount).WithMessage("Amount must be 0.01 to 2000.00 AED with at most 2 decimals.");

        RuleFor(x => x.Operator)
            .NotEmpty().WithMessage("Operator is required.")
            .Must(BeValidOperator).WithMessage("Operator must be 'ETISALAT' or 'DU'.");

        RuleFor(x => x.ClientRef)
            .NotEmpty().WithMessage("clientRef is required.")
            .MaximumLength(64).WithMessage("clientRef must be at most 64 characters.");
    }

    private static bool BeValidOperator(string op) =>
        string.Equals(op, "ETISALAT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(op, "DU", StringComparison.OrdinalIgnoreCase);

    private static bool BeValidAmount(string amount)
    {
        if (string.IsNullOrWhiteSpace(amount)) return false;
        // Accept only "0.01" or "25.00" style. Reject scientific notation and commas.
        if (!decimal.TryParse(amount, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;
        if (value < 0.01m || value > 2000.00m) return false;
        // Reject more than 2 decimal places.
        var raw = amount.Trim().TrimStart('-');
        int dotIdx = raw.IndexOf('.');
        if (dotIdx >= 0 && raw.Length - dotIdx - 1 > 2) return false;
        return true;
    }
}
