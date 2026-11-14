namespace TopupLedger.Domain.Enums;

public enum PartnerStatus
{
    Active = 1,
    Suspended = 2
}

public enum AdminRole
{
    Admin = 1,
    Viewer = 2
}

public enum LedgerEntryType
{
    // Credit increases wallet balance; Debit decreases it.
    Credit = 1,
    Debit = 2
}

public enum LedgerReason
{
    TopupDebit = 1,        // Wallet debited for a top-up (face value - retailer commission).
    TopupRefund = 2,       // Refund of a top-up debit when the operator fails / never confirms.
    ManualCredit = 3,      // Manual admin credit (must include a reason).
    SettlementAdjustment = 4 // Reserved for settlement corrections.
}

public enum IdempotencyStatus
{
    InProgress = 1,
    Completed = 2
}

public enum CallbackStatus
{
    Received = 1,
    Duplicate = 2,           // Same callback delivered twice; no money moved second time.
    IgnoredAfterTerminal = 3 // Callback arrived after top-up reached terminal state.
}

public enum ReconciliationMatch
{
    Matched = 1,
    MissingInOurs = 2,        // In operator file but no corresponding top-up.
    MissingInOperator = 3,    // In our system but not in the operator file.
    AmountMismatch = 4,
    StatusMismatch = 5
}
