using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Application.Services;

/// <summary>
/// Service for atomic wallet debit/credit + ledger writes. Every method runs inside a
/// caller-supplied DB transaction (IUnitOfWorkTransaction) and takes a pessimistic
/// SELECT ... FOR UPDATE lock on the wallet row, which is what makes 200 parallel debits
/// safe: only one transaction can hold the lock at a time, the others wait, and as soon
/// as balance drops below the requested debit, they return INSUFFICIENT_FUNDS.
///
/// All money values are integer fils. Float/double must never appear here.
/// </summary>
public sealed class WalletService
{
    private readonly IWalletRepository _wallets;

    public WalletService(IWalletRepository wallets)
    {
        _wallets = wallets;
    }

    /// <summary>
    /// Atomically debits a wallet: takes the row lock, checks balance, appends a DEBIT
    /// ledger row with the new running balance, and updates the cached balance column.
    /// Throws INSUFFICIENT_FUNDS if balance &lt; debit amount (no money moves in that case).
    /// </summary>
    public async Task<(Wallet wallet, WalletLedger entry)> DebitAsync(
        long walletId, long amountFils, long topupId, LedgerReason reason,
        string? correlationId, DateTime atUtc, CancellationToken ct = default)
    {
        if (amountFils <= 0)
            throw new DomainException("VALIDATION_FAILED", "Debit amount must be positive.");

        // Lock the wallet row inside the caller's transaction.
        var wallet = await _wallets.GetByPartnerForUpdateAsync(walletId, ct)
                    ?? throw new DomainException("NOT_FOUND", $"Wallet {walletId} not found.");

        if (wallet.BalanceFils < amountFils)
            throw new DomainException("INSUFFICIENT_FUNDS",
                $"Wallet {walletId} balance {wallet.BalanceFils} fils is less than debit {amountFils} fils.");

        long newBalance = wallet.BalanceFils - amountFils;
        await _wallets.UpdateBalanceAsync(walletId, newBalance, atUtc, ct);

        var entry = new WalletLedger
        {
            WalletId = walletId,
            TopupId = topupId,
            EntryType = LedgerEntryType.Debit,
            Reason = reason,
            AmountFils = amountFils,
            RunningBalanceFils = newBalance,
            CorrelationId = correlationId,
            CreatedAtUtc = atUtc
        };
        await _wallets.AppendLedgerAsync(entry, ct);
        wallet.BalanceFils = newBalance;
        return (wallet, entry);
    }

    /// <summary>
    /// Atomically credits a wallet: appends a CREDIT ledger row, updates the balance cache.
    /// Used for refunds and manual credits. Caller is responsible for ensuring the credit
    /// happens at most once per topup (via the topup state machine).
    /// </summary>
    public async Task<(Wallet wallet, WalletLedger entry)> CreditAsync(
        long walletId, long amountFils, long topupId, LedgerReason reason,
        string? reasonText, string? correlationId, DateTime atUtc, CancellationToken ct = default)
    {
        if (amountFils <= 0)
            throw new DomainException("VALIDATION_FAILED", "Credit amount must be positive.");

        var wallet = await _wallets.GetByPartnerForUpdateAsync(walletId, ct)
                    ?? throw new DomainException("NOT_FOUND", $"Wallet {walletId} not found.");

        long newBalance = wallet.BalanceFils + amountFils;
        await _wallets.UpdateBalanceAsync(walletId, newBalance, atUtc, ct);

        var entry = new WalletLedger
        {
            WalletId = walletId,
            TopupId = topupId,
            EntryType = LedgerEntryType.Credit,
            Reason = reason,
            AmountFils = amountFils,
            RunningBalanceFils = newBalance,
            ReasonText = reasonText,
            CorrelationId = correlationId,
            CreatedAtUtc = atUtc
        };
        await _wallets.AppendLedgerAsync(entry, ct);
        wallet.BalanceFils = newBalance;
        return (wallet, entry);
    }
}
