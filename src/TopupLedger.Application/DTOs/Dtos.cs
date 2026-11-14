using System.Text.Json.Serialization;

namespace TopupLedger.Application.DTOs;

public sealed record CreateTopupRequest
{
    public string Msisdn { get; init; } = string.Empty;
    /// <summary>Decimal AED amount as a string (e.g. "25.00"). Parsed to integer fils server-side.</summary>
    public string Amount { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;  // "ETISALAT" | "DU" (case-insensitive)
    public string ClientRef { get; init; } = string.Empty;
}

public sealed record CreateTopupResponse
{
    public long Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Msisdn { get; init; } = string.Empty;
    public long FaceValueFils { get; init; }
    public string FaceValueAed { get; init; } = string.Empty;
    public long RetailerCommissionFils { get; init; }
    public string RetailerCommissionAed { get; init; } = string.Empty;
    public long DistributorShareFils { get; init; }
    public string DistributorShareAed { get; init; } = string.Empty;
    public long WalletDebitFils { get; init; }
    public string WalletDebitAed { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string ClientRef { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime BusinessDate { get; init; }
}

public sealed record TopupDetailResponse
{
    public long Id { get; init; }
    public long PartnerId { get; init; }
    public string PartnerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string MsisdnMasked { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string ClientRef { get; init; } = string.Empty;
    public long FaceValueFils { get; init; }
    public string FaceValueAed { get; init; } = string.Empty;
    public long RetailerCommissionFils { get; init; }
    public string RetailerCommissionAed { get; init; } = string.Empty;
    public long DistributorShareFils { get; init; }
    public string DistributorShareAed { get; init; } = string.Empty;
    public long WalletDebitFils { get; init; }
    public string WalletDebitAed { get; init; } = string.Empty;
    public string? OperatorTxnId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime BusinessDate { get; init; }
    public DateTime? FinalStateAtUtc { get; init; }
    public DateTime? CallbackReceivedAtUtc { get; init; }
}

public sealed record TopupListItem
{
    public long Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public string MsisdnMasked { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string ClientRef { get; init; } = string.Empty;
    public long FaceValueFils { get; init; }
    public string FaceValueAed { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed record ListTopupsResponse
{
    public IReadOnlyList<TopupListItem> Items { get; init; } = Array.Empty<TopupListItem>();
    public string? NextCursor { get; init; }
    public int Count => Items.Count;
}

public sealed record WalletResponse
{
    public long WalletId { get; init; }
    public long PartnerId { get; init; }
    public long BalanceFils { get; init; }
    public string BalanceAed { get; init; } = string.Empty;
    public string Currency { get; init; } = "AED";
    public IReadOnlyList<LedgerEntryDto> RecentEntries { get; init; } = Array.Empty<LedgerEntryDto>();
}

public sealed record LedgerEntryDto
{
    public long Id { get; init; }
    public string EntryType { get; init; } = string.Empty;   // "CREDIT" / "DEBIT"
    public string Reason { get; init; } = string.Empty;
    public long AmountFils { get; init; }
    public string AmountAed { get; init; } = string.Empty;
    public long RunningBalanceFils { get; init; }
    public string RunningBalanceAed { get; init; } = string.Empty;
    public string? ReasonText { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed record OperatorCallbackRequest
{
    public string? OperatorTxnId { get; init; }
    public long TopupId { get; init; }
    public string Status { get; init; } = string.Empty;  // "SUCCESS" / "FAILED"
}

public sealed record SettlementResponse
{
    public long PartnerId { get; init; }
    public string PartnerName { get; init; } = string.Empty;
    public DateTime BusinessDate { get; init; }
    public long SuccessCount { get; init; }
    public long FailedCount { get; init; }
    public long ExpiredCount { get; init; }
    public long PendingCount { get; init; }
    public long TotalFaceValueFils { get; init; }
    public string TotalFaceValueAed { get; init; } = string.Empty;
    public long TotalRetailerCommissionFils { get; init; }
    public string TotalRetailerCommissionAed { get; init; } = string.Empty;
    public long TotalDistributorShareFils { get; init; }
    public string TotalDistributorShareAed { get; init; } = string.Empty;
    public long NetDebitFils { get; init; }
    public string NetDebitAed { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
}

public static class MoneyFormatter
{
    /// <summary>
    /// Formats integer fils as an AED string with two decimals, e.g. 2500 -> "25.00".
    /// </summary>
    public static string ToAed(long fils)
    {
        bool negative = fils < 0;
        long abs = Math.Abs(fils);
        long aed = abs / 100;
        long rem = abs % 100;
        string s = $"{aed}.{rem:D2}";
        return negative ? "-" + s : s;
    }
}
