namespace TopupLedger.Application.Exceptions;

/// <summary>
/// Domain-layer exception carrying a stable error code (matching the assignment's
/// error-code table) that the API layer maps to the appropriate HTTP status and
/// RFC 7807 problem-details body.
/// </summary>
public sealed class DomainException : Exception
{
    public string Code { get; }
    public int HttpStatusCode { get; }
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    public DomainException(string code, string message,
        int? httpStatusCode = null,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(message)
    {
        Code = code;
        HttpStatusCode = httpStatusCode ?? MapStatusCode(code);
        FieldErrors = fieldErrors ?? new Dictionary<string, string[]>();
    }

    public static int MapStatusCode(string code) => code switch
    {
        "VALIDATION_FAILED" => 400,
        "AUTH_INVALID_SIGNATURE" => 401,
        "AUTH_CLOCK_SKEW" => 401,
        "AUTH_MISSING_HEADERS" => 401,
        "ADMIN_AUTH_FAILED" => 401,
        "INSUFFICIENT_FUNDS" => 402,
        "PARTNER_SUSPENDED" => 403,
        "FORBIDDEN" => 403,
        "NOT_FOUND" => 404,
        "REQUEST_IN_PROGRESS" => 409,
        "DUPLICATE_CLIENT_REF" => 409,
        "IDEMPOTENCY_KEY_REUSED" => 422,
        "RATE_LIMITED" => 429,
        _ => 400
    };
}

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}

public sealed class UniqueConstraintException : Exception
{
    public UniqueConstraintException(string message) : base(message) { }
}
