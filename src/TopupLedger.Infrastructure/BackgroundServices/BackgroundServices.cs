using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Configuration;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Infrastructure.BackgroundServices;

/// <summary>
/// Simulates the external mobile operator. For every PENDING top-up created, schedules a
/// callback after a random delay (1-15s) with SUCCESS (85%), FAILED (10%) or no callback (5%).
/// The callback is delivered as an HTTP POST to the local /api/v1/operator/callbacks/{operator}
/// endpoint (so the production code path is exercised end-to-end).
/// </summary>
public sealed class OperatorSimulationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<OperatorSimulationOptions> _options;
    private readonly ILogger<OperatorSimulationBackgroundService> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public OperatorSimulationBackgroundService(
        IServiceProvider services,
        IOptions<OperatorSimulationOptions> options,
        ILogger<OperatorSimulationBackgroundService> logger,
        IHttpClientFactory httpFactory)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Operator simulation is disabled. Top-ups will remain PENDING until manual callbacks.");
            return;
        }

        _logger.LogInformation("Operator simulation running: success={P1} failed={P2} no-callback={P3} delay=[{Min},{Max}]s",
            _options.Value.SuccessProbability, _options.Value.FailedProbability,
            1.0 - _options.Value.SuccessProbability - _options.Value.FailedProbability,
            _options.Value.MinDelaySeconds, _options.Value.MaxDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TopupLedgerDbContext>();

                // Find PENDING topups that we haven't scheduled yet.
                // A topup is "scheduled" when at least one callback_history row exists OR
                // it has been PENDING for at least the max delay (in which case the no-callback branch wins).
                var cutoff = DateTime.UtcNow.AddSeconds(-_options.Value.MaxDelaySeconds);
                var pending = await db.Topups
                    .Where(t => t.Status == TopupStatus.Pending && t.CreatedAtUtc >= cutoff)
                    .Where(t => !db.CallbackHistory.Any(c => c.TopupId == t.Id))
                    .OrderBy(t => t.Id)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var t in pending)
                {
                    // Don't await - schedule in parallel.
                    _ = Task.Run(() => ScheduleCallbackAsync(t.Id, t.Operator, stoppingToken), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Operator simulation loop encountered an error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ScheduleCallbackAsync(long topupId, Operator op, CancellationToken ct)
    {
        try
        {
            int delay = RandomNumberGenerator.GetInt32(_options.Value.MinDelaySeconds, _options.Value.MaxDelaySeconds + 1);
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);

            double roll = RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0;
            if (roll < _options.Value.SuccessProbability)
            {
                await FireCallbackAsync(topupId, op, "SUCCESS", ct);
            }
            else if (roll < _options.Value.SuccessProbability + _options.Value.FailedProbability)
            {
                await FireCallbackAsync(topupId, op, "FAILED", ct);
            }
            else
            {
                // 5% no-callback. The ExpireBackgroundService will mark EXPIRED after 120s.
                _logger.LogInformation("Topup {Id}: simulated no-callback (will expire).", topupId);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule operator callback for topup {Id}.", topupId);
        }
    }

    private async Task FireCallbackAsync(long topupId, Operator op, string status, CancellationToken ct)
    {
        var opCode = op.ToCode();
        var token = GetOperatorToken(opCode);
        var body = $$"""{"operatorTxnId":"SIM-{{topupId}}-{{opCode}}","topupId":{{topupId}},"status":"{{status}}"}""";

        // Use the API's own callback URL. In production, the operator would POST to this endpoint.
        var client = _httpFactory.CreateClient("operator-simulation");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/callbacks/{opCode}")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Operator-Token", token);

        try
        {
            var response = await client.SendAsync(request, ct);
            _logger.LogInformation("Operator callback fired for topup {Id} ({op}, {status}) -> HTTP {code}",
                topupId, opCode, status, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deliver operator callback for topup {Id}.", topupId);
        }
    }

    private string GetOperatorToken(string opCode)
    {
        using var scope = _services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OperatorCallbackOptions>>().Value;
        return options.Tokens.TryGetValue(opCode, out var t) ? t : "dev-operator-token";
    }
}

/// <summary>
/// Marks PENDING top-ups as EXPIRED when no callback has been received within ExpireAfterSeconds
/// (default 120). Refunds the wallet exactly once per top-up via TopupService.ExpireAsync.
/// </summary>
public sealed class ExpireBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<OperatorSimulationOptions> _options;
    private readonly ILogger<ExpireBackgroundService> _logger;

    public ExpireBackgroundService(
        IServiceProvider services,
        IOptions<OperatorSimulationOptions> options,
        ILogger<ExpireBackgroundService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TopupLedgerDbContext>();
                var topupSvc = scope.ServiceProvider.GetRequiredService<TopupService>();

                var cutoff = DateTime.UtcNow.AddSeconds(-_options.Value.ExpireAfterSeconds);
                var expireCandidates = await db.Topups
                    .Where(t => t.Status == TopupStatus.Pending && t.CreatedAtUtc <= cutoff)
                    .OrderBy(t => t.Id)
                    .Take(200)
                    .Select(t => t.Id)
                    .ToListAsync(stoppingToken);

                foreach (var id in expireCandidates)
                {
                    try
                    {
                        await topupSvc.ExpireAsync(id, $"expire-worker-{Guid.NewGuid():N}", stoppingToken);
                        _logger.LogInformation("Topup {Id} marked EXPIRED (no callback within {s}s).", id, _options.Value.ExpireAfterSeconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to expire topup {Id}.", id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Expire worker loop encountered an error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.Value.ExpirePollSeconds), stoppingToken);
        }
    }
}

/// <summary>
/// Development-only seeder. Runs at app startup. For each partner whose secret_cipher is empty
/// (created by db/seed.sql with placeholders), generates a fresh HMAC secret, encrypts it via
/// AES-GCM, persists the ciphertext, and logs the plaintext secret ONCE to the console
/// ("API secret shown only once at creation" event per the assignment).
/// </summary>
public sealed class DevSecretSeeder : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DevSecretSeeder> _logger;
    private readonly bool _enabled;

    public DevSecretSeeder(IServiceProvider services, ILogger<DevSecretSeeder> logger,
        IHostEnvironment env)
    {
        _services = services;
        _logger = logger;
        _enabled = env.IsDevelopment();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("DevSecretSeeder skipped (not in Development environment).");
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TopupLedgerDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IPartnerSecretProtector>();

            var partnersMissingSecret = await db.Partners
                .Where(p => p.SecretCipher.Length == 0)
                .OrderBy(p => p.Id)
                .ToListAsync(cancellationToken);

            if (partnersMissingSecret.Count == 0)
            {
                _logger.LogDebug("All partners already have encrypted secrets; nothing to seed.");
                return;
            }

            _logger.LogWarning("Generating HMAC secrets for {n} partners. Plaintext secrets are shown BELOW ONLY ONCE - copy them now.",
                partnersMissingSecret.Count);

            foreach (var p in partnersMissingSecret)
            {
                string plaintext = "sk_dev_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
                var (cipher, nonce, tag) = protector.Encrypt(plaintext);
                p.SecretCipher = cipher;
                p.SecretNonce = nonce;
                p.SecretTag = tag;
            }

            await db.SaveChangesAsync(cancellationToken);

            foreach (var p in partnersMissingSecret)
            {
                // Re-decrypt to display (proves roundtrip works).
                string plaintext = protector.Decrypt(p.SecretCipher, p.SecretNonce, p.SecretTag);
                _logger.LogWarning("DEV ONLY - Partner '{name}' (api_key='{api}') secret (shown once): {secret}",
                    p.Name, p.ApiKey, plaintext);
            }

            // Also fix up dev admin user password hashes (the seed.sql placeholder hashes are NOT real BCrypt,
            // so login would fail; we generate proper hashes for the known dev passwords and update them in place).
            await EnsureDevAdminPasswordAsync(db, "admin", "Admin@123", Domain.Enums.AdminRole.Admin, cancellationToken);
            await EnsureDevAdminPasswordAsync(db, "viewer", "Viewer@123", Domain.Enums.AdminRole.Viewer, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DevSecretSeeder failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureDevAdminPasswordAsync(TopupLedgerDbContext db, string username, string plaintextPassword, Domain.Enums.AdminRole role, CancellationToken ct)
    {
        var existing = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
        bool needsHash = existing == null
                        || !BCrypt.Net.BCrypt.Verify(plaintextPassword, existing.PasswordHash);
        if (!needsHash) return;

        string hash = BCrypt.Net.BCrypt.HashPassword(plaintextPassword, workFactor: 11);
        if (existing == null)
        {
            db.AdminUsers.Add(new Domain.Entities.AdminUser
            {
                Username = username,
                PasswordHash = hash,
                Role = role,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            existing.PasswordHash = hash;
            existing.Role = role;
        }
        await db.SaveChangesAsync(ct);
    }
}
