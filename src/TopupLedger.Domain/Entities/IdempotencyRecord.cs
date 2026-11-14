using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Database-backed idempotency record. Same partner + same key + same body hash replays the
/// original response; same key with a different body returns 422 IDEMPOTENCY_KEY_REUSED.
/// While the first request is in progress (status InProgress), an incoming same-key request
/// returns 409 REQUEST_IN_PROGRESS. Records expire after 24 hours.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }
    public long PartnerId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestBodySha256 { get; set; } = string.Empty;  // lowercase hex
    public string? ClientRef { get; set; }                          // captured to detect DUPLICATE_CLIENT_REF
    public IdempotencyStatus Status { get; set; }
    public int ResponseStatus { get; set; }                          // HTTP status code returned
    public string? ResponseBodyJson { get; set; }                    // captured response body to replay
    public string? ResourceLocation { get; set; }                    // Location header value, if any
    public long? TopupId { get; set; }                                // topup created, if any
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }                       // CreatedAt + 24h
}
