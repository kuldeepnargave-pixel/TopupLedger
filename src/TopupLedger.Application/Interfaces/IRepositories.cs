using TopupLedger.Domain.Entities;

namespace TopupLedger.Application.Interfaces;

public interface IPartnerRepository
{
    Task<Partner?> GetByApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<Partner?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Partner>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<long> CreateAsync(Partner partner, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, Domain.Enums.PartnerStatus status, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
}

public interface IWalletRepository
{
    /// <summary>
    /// Returns the wallet for a partner, taking a pessimistic row lock (SELECT ... FOR UPDATE)
    /// when called inside an open transaction. The lock is what makes 200 parallel debits safe.
    /// </summary>
    Task<Wallet?> GetByPartnerForUpdateAsync(long partnerId, CancellationToken ct = default);
    Task<Wallet?> GetByPartnerAsync(long partnerId, CancellationToken ct = default);
    Task<long> GetBalanceAsync(long walletId, CancellationToken ct = default);
    Task<IReadOnlyList<WalletLedger>> GetRecentEntriesAsync(long walletId, int count, CancellationToken ct = default);
    Task<IReadOnlyList<WalletLedger>> GetEntriesInRangeAsync(long walletId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    /// <summary>
    /// Computes the wallet balance at a past instant by SUMming ledger rows created up to and
    /// excluding that instant. Used by the historical balance report.
    /// </summary>
    Task<long> GetBalanceAtAsync(long walletId, DateTime asOfUtcExclusive, CancellationToken ct = default);
    Task UpdateBalanceAsync(long walletId, long newBalanceFils, DateTime updatedAtUtc, CancellationToken ct = default);
    Task AppendLedgerAsync(WalletLedger entry, CancellationToken ct = default);
}

public interface ITopupRepository
{
    Task<Topup?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Topup?> GetByIdForUpdateAsync(long id, CancellationToken ct = default);
    Task<Topup?> GetByPartnerAndClientRefAsync(long partnerId, string clientRef, CancellationToken ct = default);
    Task<Topup?> GetByOperatorTxnIdAsync(string? operatorTxnId, CancellationToken ct = default);
    Task<long> CreateAsync(Topup topup, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, Domain.Enums.TopupStatus newStatus, DateTime? finalStateAtUtc, string? operatorTxnId, DateTime? callbackReceivedAtUtc, CancellationToken ct = default);
    /// <summary>
    /// Keyset pagination. Cursor is "{createdAtUtc:O},{id}" (URL-safe base64). Returns
    /// topups for the partner strictly older than the cursor (by created_at_utc, then by id).
    /// </summary>
    Task<(IReadOnlyList<Topup> items, string? nextCursor)> ListAsync(
        long partnerId, Domain.Enums.TopupStatus? status, DateTime? fromUtc, DateTime? toUtc,
        int limit, string? cursor, CancellationToken ct = default);
    Task<long> CountByBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default);
    Task<Dictionary<Domain.Enums.TopupStatus, long>> CountByStatusForBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default);
    Task<Dictionary<Domain.Enums.Operator, long>> CountByOperatorForBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default);
}

public interface ICommissionRuleRepository
{
    Task<IReadOnlyList<CommissionRule>> GetAllAsync(CancellationToken ct = default);
}

public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> GetAsync(long partnerId, string idempotencyKey, CancellationToken ct = default);
    /// <summary>
    /// Inserts an InProgress record. Throws UniqueConstraintException on (partner_id, idempotency_key) collision,
    /// which is caught by the caller to convert to REQUEST_IN_PROGRESS (when the existing record is still InProgress)
    /// or IDEMPOTENCY_KEY_REUSED (when the body hash differs).
    /// </summary>
    Task InsertInProgressAsync(IdempotencyRecord record, CancellationToken ct = default);
    Task MarkCompletedAsync(long id, int responseStatus, string? responseBodyJson, string? resourceLocation, long? topupId, DateTime completedAtUtc, CancellationToken ct = default);
    Task<int> DeleteExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default);
}

public interface ICallbackHistoryRepository
{
    Task AppendAsync(CallbackHistory entry, CancellationToken ct = default);
    Task<IReadOnlyList<CallbackHistory>> ListForTopupAsync(long topupId, CancellationToken ct = default);
}

public interface ISettlementRepository
{
    Task<Settlement?> GetAsync(long partnerId, DateTime businessDateUtc, CancellationToken ct = default);
    Task<long> CreateAsync(Settlement settlement, IEnumerable<SettlementItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<Settlement>> ListForDayAsync(DateTime businessDateUtc, CancellationToken ct = default);
}

public interface IReconciliationRepository
{
    Task<long> CreateFileAsync(OperatorReconciliationFile file, CancellationToken ct = default);
    Task<OperatorReconciliationFile?> FindDuplicateAsync(string fileName, string sha256, Domain.Enums.Operator op, CancellationToken ct = default);
    Task<int> AppendRowsAsync(IEnumerable<OperatorReconciliationRow> rows, CancellationToken ct = default);
    Task<IReadOnlyList<OperatorReconciliationFile>> ListFilesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<OperatorReconciliationFile?> GetFileAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<OperatorReconciliationRow>> GetRowsAsync(long fileId, CancellationToken ct = default);
}

public interface IAdminUserRepository
{
    Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task UpdateLastLoginAsync(long id, DateTime atUtc, CancellationToken ct = default);
}
