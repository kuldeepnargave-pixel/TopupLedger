
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Application.Services;

/// <summary>
/// Service for atomic wallet debit/credit + ledger writes.
/// All money values are integer fils.
/// </summary>
public sealed class WalletService
{
    private readonly IWalletRepository _wallets;

    public WalletService(IWalletRepository wallets)
    {
        _wallets = wallets;
    }

    /// <summary>
    /// Atomically debits a partner wallet.
    /// Uses PartnerId to find the wallet and Wallet.Id for updates and ledger entries.
    /// </summary>
    public async Task<(Wallet wallet, WalletLedger entry)> DebitAsync(
        long partnerId,
        long amountFils,
        long topupId,
        LedgerReason reason,
        string? correlationId,
        DateTime atUtc,
        CancellationToken ct = default)
    {
        if (amountFils <= 0)
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                "Debit amount must be positive.");
        }

        // Find and lock wallet using PartnerId.
        var wallet = await _wallets.GetByPartnerForUpdateAsync(partnerId, ct)
            ?? throw new DomainException(
                "NOT_FOUND",
                $"Wallet for partner {partnerId} not found.");

        if (wallet.BalanceFils < amountFils)
        {
            throw new DomainException(
                "INSUFFICIENT_FUNDS",
                $"Wallet {wallet.Id} balance {wallet.BalanceFils} fils is less than debit {amountFils} fils.");
        }

        long newBalance = wallet.BalanceFils - amountFils;

        // Update using actual WalletId.
        await _wallets.UpdateBalanceAsync(
            wallet.Id,
            newBalance,
            atUtc,
            ct);

        var entry = new WalletLedger
        {
            WalletId = wallet.Id,
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
    /// Atomically credits a partner wallet.
    /// Uses PartnerId to find the wallet and Wallet.Id for updates and ledger entries.
    /// Used for refunds and manual credits.
    /// </summary>
    public async Task<(Wallet wallet, WalletLedger entry)> CreditAsync(
        long partnerId,
        long amountFils,
        long topupId,
        LedgerReason reason,
        string? reasonText,
        string? correlationId,
        DateTime atUtc,
        CancellationToken ct = default)
    {
        if (amountFils <= 0)
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                "Credit amount must be positive.");
        }

        // Find and lock wallet using PartnerId.
        var wallet = await _wallets.GetByPartnerForUpdateAsync(partnerId, ct)
            ?? throw new DomainException(
                "NOT_FOUND",
                $"Wallet for partner {partnerId} not found.");

        long newBalance = wallet.BalanceFils + amountFils;

        // Update using actual WalletId.
        await _wallets.UpdateBalanceAsync(
            wallet.Id,
            newBalance,
            atUtc,
            ct);

        var entry = new WalletLedger
        {
            WalletId = wallet.Id,
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
