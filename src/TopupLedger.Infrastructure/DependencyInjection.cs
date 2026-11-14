using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Application.Validators;
using TopupLedger.Domain.Entities;
using TopupLedger.Infrastructure.BackgroundServices;
using TopupLedger.Infrastructure.Configuration;
using TopupLedger.Infrastructure.Data;
using TopupLedger.Infrastructure.Repositories;
using TopupLedger.Infrastructure.Secrets;

namespace TopupLedger.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTopupLedgerInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SecretsOptions>(config.GetSection(SecretsOptions.SectionName));
        services.Configure<AdminApiOptions>(config.GetSection(AdminApiOptions.SectionName));
        services.Configure<OperatorCallbackOptions>(config.GetSection(OperatorCallbackOptions.SectionName));
        services.Configure<OperatorSimulationOptions>(config.GetSection(OperatorSimulationOptions.SectionName));

        // EF Core + Pomelo MySQL
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");
        services.AddDbContext<TopupLedgerDbContext>(opts =>
            opts.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                mySql => mySql.MigrationsAssembly(typeof(TopupLedgerDbContext).Assembly.FullName)));

        services.AddScoped<ITopupLedgerDbContext>(sp =>
        {
            var ctx = sp.GetRequiredService<TopupLedgerDbContext>();
            return new TopupLedgerDbContextProxy(ctx);
        });
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IPartnerRepository, PartnerRepository>();
        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<ITopupRepository, TopupRepository>();
        services.AddScoped<ICommissionRuleRepository, CommissionRuleRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<ICallbackHistoryRepository, CallbackHistoryRepository>();
        services.AddScoped<ISettlementRepository, SettlementRepository>();
        services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();

        services.AddScoped<IPartnerSecretProtector, AesGcmPartnerSecretProtector>();
        services.AddSingleton<CommissionService>();
        services.AddSingleton<MsisdnService>();
        services.AddSingleton<BusinessDayService>();
        services.AddSingleton<CreateTopupRequestValidator>();
        services.AddScoped<WalletService>();
        services.AddScoped<TopupService>();
        services.AddScoped<TopupLedger.Infrastructure.Services.ReconciliationService>();
        services.AddScoped<TopupLedger.Infrastructure.Services.SettlementService>();
        services.AddScoped<TopupLedger.Infrastructure.Services.ReportService>();

        services.AddHttpClient("operator-simulation", c =>
        {
            c.BaseAddress = new Uri(config["OperatorSimulation:CallbackBaseUrl"] ?? "http://localhost:8080");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHostedService<DevSecretSeeder>();
        services.AddHostedService<OperatorSimulationBackgroundService>();
        services.AddHostedService<ExpireBackgroundService>();

        return services;
    }

    /// <summary>
    /// Lightweight proxy so that the application layer can use the ITopupLedgerDbContext
    /// abstraction (which exposes the same DbSets as TopupLedgerDbContext but is interface-
    /// based). For simplicity the proxy just delegates to the concrete DbContext.
    /// </summary>
    private sealed class TopupLedgerDbContextProxy : ITopupLedgerDbContext
    {
        private readonly TopupLedgerDbContext _inner;
        public TopupLedgerDbContextProxy(TopupLedgerDbContext inner) => _inner = inner;
        public IQueryable<Partner> Partners => _inner.Partners;
        public IQueryable<Wallet> Wallets => _inner.Wallets;
        public IQueryable<WalletLedger> WalletLedger => _inner.WalletLedger;
        public IQueryable<Topup> Topups => _inner.Topups;
        public IQueryable<CommissionRule> CommissionRules => _inner.CommissionRules;
        public IQueryable<IdempotencyRecord> IdempotencyRecords => _inner.IdempotencyRecords;
        public IQueryable<CallbackHistory> CallbackHistory => _inner.CallbackHistory;
        public IQueryable<Settlement> Settlements => _inner.Settlements;
        public IQueryable<OperatorReconciliationFile> ReconciliationFiles => _inner.ReconciliationFiles;
        public IQueryable<OperatorReconciliationRow> ReconciliationRows => _inner.ReconciliationRows;
        public IQueryable<AdminUser> AdminUsers => _inner.AdminUsers;
    }
}
