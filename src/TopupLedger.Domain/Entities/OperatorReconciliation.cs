using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// A file uploaded by an admin for operator reconciliation. Re-uploading the same file
/// must not create duplicate rows; we use (FileName, FileSha256, Operator) as a unique key.
/// </summary>
public class OperatorReconciliationFile
{
    public long Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;   // lowercase hex; dedup key
    public Operator Operator { get; set; }
    public DateTime BusinessDateUtc { get; set; }   // business day covered (optional, from file metadata)
    public DateTime UploadedAtUtc { get; set; }
    public long TotalRows { get; set; }
    public long MatchedCount { get; set; }
    public long MissingInOursCount { get; set; }
    public long MissingInOperatorCount { get; set; }
    public long AmountMismatchCount { get; set; }
    public long StatusMismatchCount { get; set; }
    public List<OperatorReconciliationRow> Rows { get; set; } = new();
}

public class OperatorReconciliationRow
{
    public long Id { get; set; }
    public long FileId { get; set; }
    public OperatorReconciliationFile File { get; set; } = null!;
    public string OperatorTxnId { get; set; } = string.Empty;
    public string Msisdn { get; set; } = string.Empty;
    public long AmountFils { get; set; }
    public string Status { get; set; } = string.Empty;   // raw status text from operator file
    public DateTime EventTimeDubai { get; set; }   // as parsed from CSV
    public long? MatchedTopupId { get; set; }       // our internal topup id if matched
    public ReconciliationMatch Match { get; set; } = ReconciliationMatch.MissingInOurs;
    public string? Note { get; set; }   // mismatch detail, e.g. "amount: ours=2500 operator=2550"
}
