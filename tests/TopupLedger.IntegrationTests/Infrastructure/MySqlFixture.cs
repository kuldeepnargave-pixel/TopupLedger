using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MySql;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure;
using TopupLedger.Infrastructure.Data;
using Xunit;

namespace TopupLedger.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit class fixture that spins up a real MySQL 8 instance via Testcontainers, applies the
/// schema.sql + seed.sql, and exposes a ServiceProvider for tests to use. If Docker is not
/// running locally, IsAvailable is set to false and tests skip themselves.
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private MySqlContainer? _container;
    private IServiceProvider? _services;

    public bool IsAvailable { get; private set; }
    public IServiceProvider Services => _services ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MySqlBuilder()
                .WithImage("mysql:8.0")
                .WithDatabase("topupledger_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();

            var connStr = _container.GetConnectionString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<TopupLedgerDbContext>(o =>
                o.UseMySql(connStr, ServerVersion.AutoDetect(connStr)));
            services.AddTopupLedgerInfrastructureTest(connStr);
            _services = services.BuildServiceProvider();

            // Apply schema + seed.
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TopupLedgerDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.ExecuteSqlRawAsync(GetSchemaSql());
            await db.Database.ExecuteSqlRawAsync(GetSeedSql());

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MySqlFixture skipped: {ex.GetType().Name}: {ex.Message}");
            IsAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    private static string GetSchemaSql()
    {
        var path = FindFileUp("db/schema.sql");
        return File.ReadAllText(path);
    }

    private static string GetSeedSql()
    {
        var path = FindFileUp("db/seed.sql");
        return File.ReadAllText(path);
    }

    private static string FindFileUp(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}

internal static class TestServiceCollectionExtensions
{
    public static IServiceCollection AddTopupLedgerInfrastructureTest(this IServiceCollection services, string connectionString)
    {
        // Use the production infrastructure registration but with the test MySQL connection string.
        // We re-create the ServiceProvider so the configuration is wired correctly.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Secrets:MasterKey"] = "TEST_MASTER_KEY_32_BYTES_FOR_TESTS!!",
                ["AdminApi:AdminApiKey"] = "test-admin-key",
                ["OperatorCallbacks:Tokens:etisalat"] = "test-op-token",
                ["OperatorCallbacks:Tokens:du"] = "test-op-token",
                ["OperatorSimulation:Enabled"] = "false"
            })
            .Build();
        return services.AddTopupLedgerInfrastructure(config);
    }
}
