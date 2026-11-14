using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TopupLedger.Application.Interfaces;

namespace TopupLedger.Infrastructure.Data;

/// <summary>
/// EF Core unit of work. BeginTransactionAsync returns a wrapper around IDbContextTransaction
/// that commits/rolls back the underlying EF transaction. Repositories that take part in a
/// transaction must share the same DbContext instance via DI (Scoped lifetime).
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly TopupLedgerDbContext _db;
    public EfUnitOfWork(TopupLedgerDbContext db) => _db = db;

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => new EfTransaction(await _db.Database.BeginTransactionAsync(ct));

    public void Dispose() => _db.Dispose();

    public sealed class EfTransaction : IUnitOfWorkTransaction
    {
        private readonly IDbContextTransaction _txn;
        public EfTransaction(IDbContextTransaction txn) => _txn = txn;
        public Task CommitAsync(CancellationToken ct = default) => _txn.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => _txn.RollbackAsync(ct);
        public ValueTask DisposeAsync() => _txn.DisposeAsync();
    }
}
