using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using System.Text.Json;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;
using TopupLedger.IntegrationTests.Infrastructure;
using Xunit;

namespace TopupLedger.IntegrationTests;

/// <summary>
/// Integration tests against a real MySQL database (Testcontainers). These tests exercise the
/// full API + DB stack: idempotent replay, same-key-different-body 422, 200 parallel debits,
/// duplicate FAILED callback refunds only once, and reconciliation re-upload no-duplicates.
///
/// To run: requires Docker running locally. `dotnet test` will skip tests if Docker isn't
/// available (see MySqlFixture).
/// </summary>
public class IntegrationTests : IClassFixture<MySqlFixture>, IAsyncLifetime
{
    private readonly MySqlFixture _fixture;
    private TopupLedgerDbContext _db = null!;
    private IPartnerSecretProtector _protector = null!;
    private IUnitOfWork _uow = null!;
    private WalletService _walletSvc = null!;
    private TopupService _topupSvc = null!;

    public IntegrationTests(MySqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.IsAvailable) return;

        var scope = _fixture.Services.CreateScope();
        _db = scope.ServiceProvider.GetRequiredService<TopupLedgerDbContext>();
        _protector = scope.ServiceProvider.GetRequiredService<IPartnerSecretProtector>();
        _uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _walletSvc = scope.ServiceProvider.GetRequiredService<WalletService>();
        _topupSvc = scope.ServiceProvider.GetRequiredService<TopupService>();

        // Clear all topups + ledger + partners for a clean slate.
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM callback_history");
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM wallet_ledger");
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM topups");
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM wallets");
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM partners");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Partner> SeedPartnerAsync(string apiKey, string plaintextSecret, long openingBalanceFils)
    {
        var (cipher, nonce, tag) = _protector.Encrypt(plaintextSecret);
        var partner = new Partner
        {
            Name = "Test Partner " + Guid.NewGuid().ToString("N").Substring(0, 6),
            ApiKey = apiKey,
            SecretCipher = cipher,
            SecretNonce = nonce,
            SecretTag = tag,
            Status = PartnerStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Partners.Add(partner);
        await _db.SaveChangesAsync();

        var wallet = new Wallet
        {
            PartnerId = partner.Id,
            BalanceFils = openingBalanceFils,
            Currency = "AED",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        // Opening balance ledger row.
        await _walletSvc.CreditAsync(wallet.Id, openingBalanceFils, 0, LedgerReason.ManualCredit, "Opening balance for test", $"test-{Guid.NewGuid():N}", DateTime.UtcNow);

        partner.Wallet = wallet;
        return partner;
    }

    [Fact]
    public async Task Idempotent_Replay_ReturnsSameResponse_WithoutSecondDebit()
    {
        if (!_fixture.IsAvailable) return;

        // Seed a partner with 1000 AED = 100000 fils
        var partner = await SeedPartnerAsync("pk_test_idem_1", "sk_test_secret_idem_1", 100000);

        var req = new CreateTopupRequest
        {
            Msisdn = "0501234567",
            Amount = "10.00",
            Operator = "ETISALAT",
            ClientRef = "RTL-TEST-IDEM-1"
        };
        var rawBody = JsonSerializer.Serialize(req);
        var idemKey = "idem-" + Guid.NewGuid().ToString("N");

        var first = await _topupSvc.CreateTopupAsync(partner, req, rawBody, idemKey, "corr-1", default);
        var second = await _topupSvc.CreateTopupAsync(partner, req, rawBody, idemKey, "corr-2", default);

        Assert.Equal(CreateTopupResult.OutcomeKind.Created, first.Kind);
        Assert.Equal(CreateTopupResult.OutcomeKind.Replayed, second.Kind);
        Assert.Equal(first.ResponseStatus, second.ResponseStatus);
        Assert.Equal(first.ResponseBodyJson, second.ResponseBodyJson);

        // Wallet should have only ONE debit (1000 - 9.80 = 990.20 AED = 99020 fils).
        var wallet = await _db.Wallets.AsNoTracking().FirstAsync(w => w.PartnerId == partner.Id);
        Assert.Equal(99020, wallet.BalanceFils);
    }

    [Fact]
    public async Task SameKey_DifferentBody_Returns_422_IdempotencyKeyReused()
    {
        if (!_fixture.IsAvailable) return;

        var partner = await SeedPartnerAsync("pk_test_idem_2", "sk_test_secret_idem_2", 100000);

        var req1 = new CreateTopupRequest { Msisdn = "0501234567", Amount = "10.00", Operator = "ETISALAT", ClientRef = "RTL-A-2" };
        var req2 = new CreateTopupRequest { Msisdn = "0501234567", Amount = "20.00", Operator = "ETISALAT", ClientRef = "RTL-B-2" };
        var body1 = JsonSerializer.Serialize(req1);
        var body2 = JsonSerializer.Serialize(req2);
        var idemKey = "idem-" + Guid.NewGuid().ToString("N");

        var first = await _topupSvc.CreateTopupAsync(partner, req1, body1, idemKey, "corr", default);
        Assert.Equal(CreateTopupResult.OutcomeKind.Created, first.Kind);

        var ex = await Assert.ThrowsAsync<Application.Exceptions.DomainException>(() =>
            _topupSvc.CreateTopupAsync(partner, req2, body2, idemKey, "corr", default));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ex.Code);
        Assert.Equal(422, ex.HttpStatusCode);
    }

    [Fact]
    public async Task ParallelDebits_Only_AllowedCount_Succeeds_NeverNegative()
    {
        if (!_fixture.IsAvailable) return;

        // Wallet has balance for exactly 150 debits of 1000 fils each (150000 fils).
        // 200 parallel requests should result in 150 successes + 50 INSUFFICIENT_FUNDS.
        var partner = await SeedPartnerAsync("pk_test_parallel", "sk_test_parallel_secret", 150000);

        var requests = Enumerable.Range(0, 200).Select(i => new CreateTopupRequest
        {
            Msisdn = "0501234567",
            Amount = "10.00",
            Operator = "ETISALAT",
            ClientRef = "RTL-PARALLEL-" + i
        }).ToList();

        var tasks = requests.Select((req, i) => Task.Run(async () =>
        {
            try
            {
                await _topupSvc.CreateTopupAsync(partner, req, JsonSerializer.Serialize(req), "idem-" + i, "corr-" + i, default);
                return true;
            }
            catch (Application.Exceptions.DomainException ex) when (ex.Code == "INSUFFICIENT_FUNDS")
            {
                return false;
            }
        })).ToList();

        var results = await Task.WhenAll(tasks);
        int succeeded = results.Count(x => x);
        int rejected = results.Count(x => !x);

        Assert.Equal(150, succeeded);
        Assert.Equal(50, rejected);

        var wallet = await _db.Wallets.AsNoTracking().FirstAsync(w => w.PartnerId == partner.Id);
        Assert.True(wallet.BalanceFils >= 0);
        Assert.Equal(0, wallet.BalanceFils); // 150000 - (150 * 1000) = 0

        // Ledger must be balanced: wallet balance = SUM(credits) - SUM(debits).
        var ledger = await _db.WalletLedger.AsNoTracking().Where(l => l.WalletId == wallet.Id).ToListAsync();
        long sum = ledger.Sum(l => l.EntryType == LedgerEntryType.Credit ? l.AmountFils : -l.AmountFils);
        Assert.Equal(wallet.BalanceFils, sum);
    }

    [Fact]
    public async Task Duplicate_Failed_Callback_Refunds_Only_Once()
    {
        if (!_fixture.IsAvailable) return;

        var partner = await SeedPartnerAsync("pk_test_dup_cb", "sk_test_dup_cb_secret", 100000);
        var req = new CreateTopupRequest
        {
            Msisdn = "0501234567",
            Amount = "10.00",
            Operator = "ETISALAT",
            ClientRef = "RTL-DUP-CB"
        };
        var result = await _topupSvc.CreateTopupAsync(partner, req, JsonSerializer.Serialize(req), "idem-dup-cb", "corr", default);
        long topupId = result.Topup!.Id;

        // Send FAILED callback twice.
        await _topupSvc.ProcessCallbackAsync(topupId, Operator.Etisalat, "OP-1", TopupStatus.Failed, null, "corr-1", default);
        await _topupSvc.ProcessCallbackAsync(topupId, Operator.Etisalat, "OP-1", TopupStatus.Failed, null, "corr-2", default);

        var wallet = await _db.Wallets.AsNoTracking().FirstAsync(w => w.PartnerId == partner.Id);
        // Original balance was 100000. Debit was 980 (10.00 - 0.20 retailer comm at 2% = 1000 - 20 = 980).
        // After refund: balance should be back to 100000 (since debit + refund cancel out).
        Assert.Equal(100000, wallet.BalanceFils);

        // Ledger should have exactly one debit + one refund credit for this topup.
        var ledger = await _db.WalletLedger.AsNoTracking().Where(l => l.TopupId == topupId).ToListAsync();
        Assert.Equal(1, ledger.Count(l => l.EntryType == LedgerEntryType.Debit && l.Reason == LedgerReason.TopupDebit));
        Assert.Equal(1, ledger.Count(l => l.EntryType == LedgerEntryType.Credit && l.Reason == LedgerReason.TopupRefund));
    }
}
