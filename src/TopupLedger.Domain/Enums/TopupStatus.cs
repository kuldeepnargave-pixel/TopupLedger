namespace TopupLedger.Domain.Enums;

/// <summary>
/// Lifecycle states of a top-up transaction. Terminal states (SUCCESS, FAILED, EXPIRED)
/// must never change once reached.
/// </summary>
public enum TopupStatus
{
    Pending = 1,
    Success = 2,
    Failed = 3,
    Expired = 4
}

/// <summary>
/// Allowed transitions: PENDING -> SUCCESS | FAILED | EXPIRED. Anything else is rejected.
/// </summary>
public static class TopupStatusTransitions
{
    public static bool CanTransition(TopupStatus from, TopupStatus to)
    {
        if (from == to) return false;
        return from == TopupStatus.Pending &&
               (to == TopupStatus.Success || to == TopupStatus.Failed || to == TopupStatus.Expired);
    }
}
