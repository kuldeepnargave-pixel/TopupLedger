using System.Text.RegularExpressions;
using TopupLedger.Application.Exceptions;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Application.Services;

/// <summary>
/// UAE MSISDN validation and normalisation.
///
/// Accepted input formats (spaces and dashes allowed throughout):
///   05XXXXXXXX    (10 digits, local)
///   5XXXXXXXX     (9 digits, local without leading 0)
///   +9715XXXXXXXX (12 digits after the +, international)
///   009715XXXXXXXX(14 digits, international via double-zero)
///
/// Normalised output: 9715XXXXXXXX (12 digits, no separators).
/// Landline numbers (those not starting with 05 after the country code) are rejected.
///
/// Mobile prefixes -> operator:
///   050 / 054 / 056 -> Etisalat
///   052 / 055 / 058 -> du
/// </summary>
public sealed class MsisdnService
{
    // Strip every character that is not a digit.
    private static readonly Regex NonDigit = new(@"[^\d]", RegexOptions.Compiled);

    // 9715 + 8 digits = 12 digits total in the normalised form.
    private static readonly Regex NormalisedForm =
        new(@"^9715\d{8}$", RegexOptions.Compiled);

    private static readonly HashSet<string> EtisalatPrefixes = new() { "050", "054", "056" };
    private static readonly HashSet<string> DuPrefixes = new() { "052", "055", "058" };

    /// <summary>
    /// Normalises raw input. Throws DomainException with code VALIDATION_FAILED on bad input.
    /// </summary>
    public string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new DomainException("VALIDATION_FAILED", "MSISDN is required.");

        string digits = NonDigit.Replace(raw, "");

        // Strip the country code in any of its forms. We do not trust the "+" alone -
        // we also strip a leading 00971 or 971 then ensure the rest begins with 5.
        if (digits.StartsWith("00971")) digits = digits.Substring(5);
        else if (digits.StartsWith("971")) digits = digits.Substring(3);

        // Now `digits` should be 05XXXXXXXX (10) or 5XXXXXXXX (9). Normalise to 05XXXXXXXX.
        if (digits.Length == 9 && digits.StartsWith("5")) digits = "0" + digits;
        if (digits.Length != 10 || !digits.StartsWith("05"))
            throw new DomainException("VALIDATION_FAILED",
                $"MSISDN '{MaskForLog(digits)}' is not a valid UAE mobile number. Expected 05XXXXXXXX / 5XXXXXXXX / +9715XXXXXXXX / 009715XXXXXXXX.");

        // Landline / non-mobile check: anything starting with 05 is a mobile by definition
        // of the prefix map above, so the only landline case is when input did not start with
        // 05 after stripping country code (handled above).

        string normalised = "971" + digits;       // 97105XXXXXXXX -> 9715XXXXXXXX? No: digits already starts with 0, so result is 9710...
        // Wait: digits = "0512345678". Prepending "971" gives "9710512345678" which is 13 chars. Wrong.
        // Correct normalisation: drop the leading 0 of the local form, prepend "971".
        normalised = "971" + digits.Substring(1); // 971 + 512345678 = 971512345678 (12 digits)
        if (!NormalisedForm.IsMatch(normalised))
            throw new DomainException("VALIDATION_FAILED",
                $"MSISDN '{MaskForLog(normalised)}' is not a valid UAE mobile number (landline rejected).");

        return normalised;
    }

    /// <summary>
    /// Maps a normalised MSISDN to its operator. Throws if the prefix is unknown.
    /// The normalised form is 971 + 9-digit mobile (no leading 0), so positions 3..4 hold
    /// the 2 digits after the country code, which correspond to the last 2 digits of the
    /// local 3-digit prefix (e.g. 971559123456 -> "55" -> local prefix "055").
    /// </summary>
    public Operator ResolveOperator(string normalisedMsisdn)
    {
        if (string.IsNullOrEmpty(normalisedMsisdn) || normalisedMsisdn.Length < 5)
            throw new DomainException("VALIDATION_FAILED", "MSISDN too short.");
        // The local prefix is "0" + the 2 digits after the country code "971".
        string localPrefix = "0" + normalisedMsisdn.Substring(3, 2);
        if (EtisalatPrefixes.Contains(localPrefix)) return Operator.Etisalat;
        if (DuPrefixes.Contains(localPrefix)) return Operator.Du;
        throw new DomainException("VALIDATION_FAILED",
            $"MSISDN prefix {localPrefix} does not map to any UAE mobile operator.");
    }

    /// <summary>
    /// Returns true if the requested operator matches the MSISDN's prefix-implied operator.
    /// Used by the topup validation: mismatches return 400 VALIDATION_FAILED.
    /// </summary>
    public bool OperatorMatchesPrefix(string normalisedMsisdn, Operator requested)
    {
        try
        {
            return ResolveOperator(normalisedMsisdn) == requested;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>
    /// Masks a normalised or partial MSISDN for logging: 97150*** ***567 style.
    /// Keeps the first 5 and last 3 digits, replaces the middle with asterisks.
    /// </summary>
    public static string MaskForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var digits = NonDigit.Replace(value, "");
        if (digits.Length < 8) return new string('*', digits.Length);
        return digits.Substring(0, 5)
               + new string('*', digits.Length - 8)
               + digits.Substring(digits.Length - 3);
    }

    /// <summary>
    /// Masked form for display in reports / portal: "97150* ***567".
    /// </summary>
    public static string MaskForDisplay(string normalisedMsisdn)
    {
        if (string.IsNullOrEmpty(normalisedMsisdn) || normalisedMsisdn.Length < 8) return normalisedMsisdn;
        return normalisedMsisdn.Substring(0, 5) + "* ***" + normalisedMsisdn.Substring(normalisedMsisdn.Length - 3);
    }
}
