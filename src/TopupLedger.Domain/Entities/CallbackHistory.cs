using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Record of every operator callback received, including duplicates and late arrivals.
/// Used by the Transactions detail page and by reconciliation reporting.
/// </summary>
public class CallbackHistory
{
    public long Id { get; set; }
    public long TopupId { get; set; }
    public Topup Topup { get; set; } = null!;
    public Operator Operator { get; set; }
    public string? OperatorTxnId { get; set; }
    public TopupStatus ReportedStatus { get; set; }
    public CallbackStatus Outcome { get; set; }   // Received | Duplicate | IgnoredAfterTerminal
    public DateTime ReceivedAtUtc { get; set; }
    public string? RawBody { get; set; }          // original callback body (sanitized - no secrets)
}
