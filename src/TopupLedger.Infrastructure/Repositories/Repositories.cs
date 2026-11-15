using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Infrastructure.Repositories;

public sealed class PartnerRepository : IPartnerRepository
{
    private readonly TopupLedgerDbContext _db;
    public PartnerRepository(TopupLedgerDbContext db) => _db = db;

    public async Task<Partner?> GetByApiKeyAsync(string apiKey, CancellationToken ct = default)
        => await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.ApiKey == apiKey, ct);

    public async Task<Partner?> GetByIdAsync(long id, CancellationToken ct = default)
        => await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Partner>> ListAsync(int page, int pageSize, CancellationToken ct = default)
        => await _db.Partners.AsNoTracking()
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

    public async Task<long> CreateAsync(Partner partner, CancellationToken ct = default)
    {
        _db.Partners.Add(partner);
        await _db.SaveChangesAsync(ct);
        return partner.Id;
    }

    public async Task UpdateStatusAsync(long id, PartnerStatus status, CancellationToken ct = default)
    {
        await _db.Partners.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, status)
                .SetProperty(p => p.SuspendedAtUtc, status == PartnerStatus.Suspended ? DateTime.UtcNow : (DateTime?)null), ct);
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
        => await _db.Partners.LongCountAsync(ct);
}

public sealed class WalletRepository : IWalletRepository
{
    private readonly TopupLedgerDbContext _db;
    public WalletRepository(TopupLedgerDbContext db) => _db = db;

    /// <summary>
    /// Locks the wallet row with SELECT ... FOR UPDATE (EF Core's FromSqlInterpolated +
    /// ForUpdateOf via Pomelo's LockHint extension, falling back to raw SQL when needed).
    /// </summary>
    public async Task<Wallet?> GetByPartnerForUpdateAsync(long partnerId, CancellationToken ct = default)
    {
        // Pomelo MySQL provider supports `ForUpdate()` extension on IQueryable.
        return await _db.Wallets.FromSqlInterpolated(
                $"SELECT * FROM wallets WHERE partner_id = {partnerId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Wallet?> GetByPartnerAsync(long partnerId, CancellationToken ct = default)
        => await _db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.PartnerId == partnerId, ct);

    public async Task<long> GetBalanceAsync(long walletId, CancellationToken ct = default)
        => await _db.Wallets.Where(w => w.Id == walletId).Select(w => w.BalanceFils).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<WalletLedger>> GetRecentEntriesAsync(long walletId, int count, CancellationToken ct = default)
        => await _db.WalletLedger.AsNoTracking()
            .Where(l => l.WalletId == walletId)
            .OrderByDescending(l => l.Id)
            .Take(count)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<WalletLedger>> GetEntriesInRangeAsync(long walletId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => await _db.WalletLedger.AsNoTracking()
            .Where(l => l.WalletId == walletId && l.CreatedAtUtc >= fromUtc && l.CreatedAtUtc < toUtc)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

    public async Task<long> GetBalanceAtAsync(long walletId, DateTime asOfUtcExclusive, CancellationToken ct = default)
    {
        // SUM(CASE WHEN entry_type=1 (credit) THEN amount ELSE -amount END)
        return await _db.WalletLedger
            .Where(l => l.WalletId == walletId && l.CreatedAtUtc < asOfUtcExclusive)
            .SumAsync(l => l.EntryType == LedgerEntryType.Credit ? l.AmountFils : -l.AmountFils, ct);
    }

    public async Task UpdateBalanceAsync(long walletId, long newBalanceFils, DateTime updatedAtUtc, CancellationToken ct = default)
    {
        await _db.Wallets.Where(w => w.Id == walletId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.BalanceFils, newBalanceFils)
                .SetProperty(w => w.UpdatedAtUtc, updatedAtUtc), ct);
    }

    public async Task AppendLedgerAsync(WalletLedger entry, CancellationToken ct = default)
    {
        await _db.WalletLedger.AddAsync(entry, ct);
        await _db.SaveChangesAsync(ct);
    }
}

public sealed class TopupRepository : ITopupRepository
{
    private readonly TopupLedgerDbContext _db;
    public TopupRepository(TopupLedgerDbContext db) => _db = db;

    public Task<Topup?> GetByIdAsync(long id, CancellationToken ct = default)
        => _db.Topups.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)!;

    public Task<Topup?> GetByIdForUpdateAsync(long id, CancellationToken ct = default)
        => _db.Topups.FromSqlInterpolated($"SELECT * FROM topups WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)!;

    public Task<Topup?> GetByPartnerAndClientRefAsync(long partnerId, string clientRef, CancellationToken ct = default)
        => _db.Topups.AsNoTracking().FirstOrDefaultAsync(t => t.PartnerId == partnerId && t.ClientRef == clientRef, ct)!;

    public Task<Topup?> GetByOperatorTxnIdAsync(string? operatorTxnId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operatorTxnId))
            return Task.FromResult<Topup?>(null);
        return _db.Topups.AsNoTracking().FirstOrDefaultAsync(t => t.OperatorTxnId == operatorTxnId, ct)!;
    }

    public async Task<long> CreateAsync(Topup topup, CancellationToken ct = default)
    {
        await _db.Topups.AddAsync(topup, ct);
        await _db.SaveChangesAsync(ct);
        return topup.Id;
    }

    public async Task UpdateStatusAsync(long id, TopupStatus newStatus, DateTime? finalStateAtUtc, string? operatorTxnId, DateTime? callbackReceivedAtUtc, CancellationToken ct = default)
    {
        await _db.Topups.Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, newStatus)
                .SetProperty(t => t.FinalStateAtUtc, finalStateAtUtc)
                .SetProperty(t => t.OperatorTxnId, operatorTxnId)
                .SetProperty(t => t.CallbackReceivedAtUtc, callbackReceivedAtUtc), ct);
    }

    /// <summary>
    /// Keyset (cursor) pagination. Cursor format: base64("{createdAtIso};{id}"). Returns the
    /// next page of topups strictly older than the cursor (by created_at_utc, then by id).
    /// </summary>
    public async Task<(IReadOnlyList<Topup> items, string? nextCursor)> ListAsync(
        long partnerId, TopupStatus? status, DateTime? fromUtc, DateTime? toUtc,
        int limit, string? cursor, CancellationToken ct = default)
    {
        DateTime? cursorTime = null;
        long? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                var parts = decoded.Split(';');
                cursorTime = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind);
                cursorId = long.Parse(parts[1]);
            }
            catch { /* invalid cursor treated as no cursor */ }
        }

        var q = _db.Topups.AsNoTracking().Where(t => t.PartnerId == partnerId);
        if (status.HasValue) q = q.Where(t => t.Status == status.Value);
        if (fromUtc.HasValue) q = q.Where(t => t.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue) q = q.Where(t => t.CreatedAtUtc <= toUtc.Value);

        if (cursorTime.HasValue && cursorId.HasValue)
        {
            // Strict ordering by (created_at_utc DESC, id DESC). Cursor points to the LAST item of the previous page.
            q = q.Where(t => t.CreatedAtUtc < cursorTime.Value
                          || (t.CreatedAtUtc == cursorTime.Value && t.Id < cursorId.Value));
        }

        var items = await q.OrderByDescending(t => t.CreatedAtUtc).ThenByDescending(t => t.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            var last = items[limit - 1];
            nextCursor = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{last.CreatedAtUtc:O};{last.Id}"));
            items = items.Take(limit).ToList();
        }
        return (items, nextCursor);
    }

    public async Task<long> CountByBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default)
        => await _db.Topups.LongCountAsync(t => t.BusinessDateUtc == businessDateUtc, ct);

    public async Task<Dictionary<TopupStatus, long>> CountByStatusForBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default)
        => await _db.Topups.Where(t => t.BusinessDateUtc == businessDateUtc)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

    public async Task<Dictionary<Operator, long>> CountByOperatorForBusinessDateAsync(DateTime businessDateUtc, CancellationToken ct = default)
        => await _db.Topups.Where(t => t.BusinessDateUtc == businessDateUtc)
            .GroupBy(t => t.Operator)
            .Select(g => new { Operator = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Operator, x => x.Count, ct);
}

public sealed class CommissionRuleRepository : ICommissionRuleRepository
{
    private readonly TopupLedgerDbContext _db;
    public CommissionRuleRepository(TopupLedgerDbContext db) => _db = db;

    public async Task<IReadOnlyList<CommissionRule>> GetAllAsync(CancellationToken ct = default)
        => await _db.CommissionRules.AsNoTracking().ToListAsync(ct);
}

public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly TopupLedgerDbContext _db;
    public IdempotencyRepository(TopupLedgerDbContext db) => _db = db;

    public async Task<IdempotencyRecord?> GetAsync(long partnerId, string idempotencyKey, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(i => i.PartnerId == partnerId && i.IdempotencyKey == idempotencyKey, ct);

    public async Task InsertInProgressAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        try
        {
            await _db.IdempotencyRecords.AddAsync(record, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintException($"Idempotency key '{record.IdempotencyKey}' already exists for partner {record.PartnerId}.");
        }
    }

    public async Task MarkCompletedAsync(long id, int responseStatus, string? responseBodyJson, string? resourceLocation, long? topupId, DateTime completedAtUtc, CancellationToken ct = default)
    {
        await _db.IdempotencyRecords.Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IdempotencyStatus.Completed)
                .SetProperty(i => i.ResponseStatus, responseStatus)
                .SetProperty(i => i.ResponseBodyJson, responseBodyJson)
                .SetProperty(i => i.ResourceLocation, resourceLocation)
                .SetProperty(i => i.TopupId, topupId)
                .SetProperty(i => i.CompletedAtUtc, completedAtUtc), ct);
    }

    public async Task<int> DeleteExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => await _db.IdempotencyRecords.Where(i => i.ExpiresAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(ct);

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Pomelo packs the MySQL duplicate-key error in MySqlException with Number 1062.
        var inner = ex.InnerException;
        if (inner == null) return false;
        var type = inner.GetType();
        var numberProp = type.GetProperty("Number");
        if (numberProp != null && numberProp.GetValue(inner) is int n)
            return n == 1062;
        return inner.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CallbackHistoryRepository : ICallbackHistoryRepository
{
    private readonly TopupLedgerDbContext _db;
    public CallbackHistoryRepository(TopupLedgerDbContext db) => _db = db;

    public async Task AppendAsync(CallbackHistory entry, CancellationToken ct = default)
    {
        await _db.CallbackHistory.AddAsync(entry, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CallbackHistory>> ListForTopupAsync(long topupId, CancellationToken ct = default)
        => await _db.CallbackHistory.AsNoTracking()
            .Where(c => c.TopupId == topupId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
}

public sealed class SettlementRepository : ISettlementRepository
{
    private readonly TopupLedgerDbContext _db;
    public SettlementRepository(TopupLedgerDbContext db) => _db = db;

    public Task<Settlement?> GetAsync(long partnerId, DateTime businessDateUtc, CancellationToken ct = default)
        => _db.Settlements.AsNoTracking().FirstOrDefaultAsync(s => s.PartnerId == partnerId && s.BusinessDateUtc == businessDateUtc, ct)!;

    public async Task<long> CreateAsync(Settlement settlement, IEnumerable<SettlementItem> items, CancellationToken ct = default)
    {
        await using var txn = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Save the parent settlement first so its database-generated ID is available.
            await _db.Settlements.AddAsync(settlement, ct);
            await _db.SaveChangesAsync(ct);

            // Assign the generated SettlementId to every child row before inserting it.
            foreach (var batch in items.Chunk(500))
            {
                foreach (var item in batch)
                {
                    item.SettlementId = settlement.Id;
                }

                await _db.SettlementItems.AddRangeAsync(batch, ct);
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }

            await txn.CommitAsync(ct);
            return settlement.Id;
        }
        catch
        {
            await txn.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<Settlement>> ListForDayAsync(DateTime businessDateUtc, CancellationToken ct = default)
        => await _db.Settlements.AsNoTracking()
            .Where(s => s.BusinessDateUtc == businessDateUtc)
            .OrderBy(s => s.PartnerId)
            .ToListAsync(ct);
}

public sealed class ReconciliationRepository : IReconciliationRepository
{
    private readonly TopupLedgerDbContext _db;
    public ReconciliationRepository(TopupLedgerDbContext db) => _db = db;

    public async Task<long> CreateFileAsync(OperatorReconciliationFile file, CancellationToken ct = default)
    {
        await _db.ReconciliationFiles.AddAsync(file, ct);
        await _db.SaveChangesAsync(ct);
        return file.Id;
    }

    public Task<OperatorReconciliationFile?> FindDuplicateAsync(string fileName, string sha256, Operator op, CancellationToken ct = default)
        => _db.ReconciliationFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.FileName == fileName && f.FileSha256 == sha256 && f.Operator == op, ct)!;

    public async Task<int> AppendRowsAsync(IEnumerable<OperatorReconciliationRow> rows, CancellationToken ct = default)
    {
        int total = 0;
        foreach (var batch in rows.Chunk(500))
        {
            await _db.ReconciliationRows.AddRangeAsync(batch, ct);
            total += await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return total;
    }

    public async Task<IReadOnlyList<OperatorReconciliationFile>> ListFilesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => await _db.ReconciliationFiles.AsNoTracking()
            .Where(f => f.UploadedAtUtc >= fromUtc && f.UploadedAtUtc <= toUtc)
            .OrderByDescending(f => f.UploadedAtUtc)
            .ToListAsync(ct);

    public Task<OperatorReconciliationFile?> GetFileAsync(long id, CancellationToken ct = default)
        => _db.ReconciliationFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)!;

    public async Task<IReadOnlyList<OperatorReconciliationRow>> GetRowsAsync(long fileId, CancellationToken ct = default)
        => await _db.ReconciliationRows.AsNoTracking()
            .Where(r => r.FileId == fileId)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
}

public sealed class AdminUserRepository : IAdminUserRepository
{
    private readonly TopupLedgerDbContext _db;
    public AdminUserRepository(TopupLedgerDbContext db) => _db = db;

    public Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => _db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct)!;

    public async Task UpdateLastLoginAsync(long id, DateTime atUtc, CancellationToken ct = default)
        => await _db.AdminUsers.Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAtUtc, atUtc), ct);
}
