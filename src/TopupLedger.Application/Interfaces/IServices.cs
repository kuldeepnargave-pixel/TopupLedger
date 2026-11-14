using TopupLedger.Domain.Entities;

namespace TopupLedger.Application.Interfaces;

/// <summary>
/// Encrypts and decrypts partner HMAC secrets at rest using AES-GCM.
/// Plaintext secrets never live in the database. See DECISIONS.md for the master key strategy.
/// </summary>
public interface IPartnerSecretProtector
{
    (byte[] cipher, byte[] nonce, byte[] tag) Encrypt(string plaintextSecret);
    string Decrypt(byte[] cipher, byte[] nonce, byte[] tag);
}

/// <summary>
/// Unit-of-work abstraction. Use BeginTransactionAsync + an async lambda to scope multi-table
/// financial writes. The TopupService uses this to make the wallet debit + ledger inserts +
/// topup row insert atomic, and to apply SELECT ... FOR UPDATE on the wallet row inside the txn.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

public interface ITopupLedgerDbContext
{
    IQueryable<Partner> Partners { get; }
    IQueryable<Wallet> Wallets { get; }
    IQueryable<WalletLedger> WalletLedger { get; }
    IQueryable<Topup> Topups { get; }
    IQueryable<CommissionRule> CommissionRules { get; }
    IQueryable<IdempotencyRecord> IdempotencyRecords { get; }
    IQueryable<CallbackHistory> CallbackHistory { get; }
    IQueryable<Settlement> Settlements { get; }
    IQueryable<OperatorReconciliationFile> ReconciliationFiles { get; }
    IQueryable<OperatorReconciliationRow> ReconciliationRows { get; }
    IQueryable<AdminUser> AdminUsers { get; }
}
