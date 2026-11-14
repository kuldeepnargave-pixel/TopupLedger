using System.Security.Cryptography;
using System.Text;

namespace TopupLedger.Application.Services;

/// <summary>
/// HMAC-SHA256 partner authentication. Computes and verifies the X-Signature header
/// using the secret associated with the partner's X-Api-Key.
///
/// String-to-sign (per the assignment, Section 5.2):
///   METHOD \n PATH \n X-Timestamp \n Idempotency-Key \n SHA256(rawBodyBytes)
/// - PATH is the request path WITHOUT query string or host. The leading '/' is included
///   when present (HTTP requests carry it in Request.Path).
/// - Idempotency-Key is the literal header value, or empty string for GET.
/// - SHA256 of the RAW request body bytes (NOT a re-serialised JSON). For GET this is the
///   SHA256 of the empty byte array.
/// - All SHA256 / HMAC outputs are LOWERCASE hex.
/// - Comparison is constant-time (CryptographicOperations.FixedTimeEquals).
/// - Timestamps with |now - X-Timestamp| > 300s are rejected with code AUTH_CLOCK_SKEW.
/// </summary>
public sealed class HmacSignatureService
{
    public const int MaxClockSkewSeconds = 300;

    /// <summary>
    /// Computes the SHA256 of the raw body bytes as lowercase hex.
    /// </summary>
    public static string HashBody(ReadOnlySpan<byte> bodyBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bodyBytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the canonical string-to-sign. PATH must include the leading slash (or be
    /// exactly the value of Request.Path with the query string stripped).
    /// </summary>
    public static string BuildStringToSign(string method, string path, string timestamp, string idempotencyKey, string bodySha256Hex)
    {
        return string.Join("\n",
            method.ToUpperInvariant(),
            path,
            timestamp,
            idempotencyKey ?? string.Empty,
            bodySha256Hex);
    }

    /// <summary>
    /// Computes the HMAC-SHA256 signature as lowercase hex.
    /// </summary>
    public static string Compute(string secret, string stringToSign)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of two hex signatures. Returns false if lengths differ.
    /// </summary>
    public static bool VerifyConstantTime(string expected, string actual)
    {
        if (expected is null || actual is null) return false;
        if (expected.Length != actual.Length) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>
    /// Returns true if the timestamp (Unix seconds, UTC) is within MaxClockSkewSeconds of `nowUtc`.
    /// </summary>
    public static bool IsTimestampValid(long timestampUnix, DateTime nowUtc)
    {
        long nowUnix = ((DateTimeOffset)nowUtc).ToUnixTimeSeconds();
        return Math.Abs(nowUnix - timestampUnix) <= MaxClockSkewSeconds;
    }
}
