using Microsoft.EntityFrameworkCore;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;

namespace TopupLedger.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for TopupLedger. Maps entities to the schema in db/schema.sql.
/// Money columns are mapped to BIGINT (long); enum columns to TINYINT.
/// All DateTime columns are UTC.
/// </summary>
public class TopupLedgerDbContext : DbContext
{
    public TopupLedgerDbContext(DbContextOptions<TopupLedgerDbContext> options) : base(options) { }

    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletLedger> WalletLedger => Set<WalletLedger>();
    public DbSet<Topup> Topups => Set<Topup>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<CallbackHistory> CallbackHistory => Set<CallbackHistory>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<SettlementItem> SettlementItems => Set<SettlementItem>();
    public DbSet<OperatorReconciliationFile> ReconciliationFiles => Set<OperatorReconciliationFile>();
    public DbSet<OperatorReconciliationRow> ReconciliationRows => Set<OperatorReconciliationRow>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Partners
        b.Entity<Partner>(e =>
        {
            e.ToTable("partners");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            e.Property(p => p.ApiKey).HasColumnName("api_key").HasMaxLength(64).IsRequired();
            e.Property(p => p.SecretCipher).HasColumnName("secret_cipher").HasColumnType("VARBINARY(512)").IsRequired();
            e.Property(p => p.SecretNonce).HasColumnName("secret_nonce").HasColumnType("VARBINARY(24)").IsRequired();
            e.Property(p => p.SecretTag).HasColumnName("secret_tag").HasColumnType("VARBINARY(16)").IsRequired();
            e.Property(p => p.DistributorId).HasColumnName("distributor_id");
            e.Property(p => p.Status).HasColumnName("status").HasConversion<int>();
            e.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(p => p.SuspendedAtUtc).HasColumnName("suspended_at_utc");
            e.HasIndex(p => p.ApiKey).IsUnique();
            e.HasOne(p => p.Distributor).WithMany().HasForeignKey(p => p.DistributorId).OnDelete(DeleteBehavior.Restrict);
        });

        // Wallets
        b.Entity<Wallet>(e =>
        {
            e.ToTable("wallets");
            e.HasKey(w => w.Id);
            e.Property(w => w.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(w => w.PartnerId).HasColumnName("partner_id");
            e.Property(w => w.BalanceFils).HasColumnName("balance_fils");
            e.Property(w => w.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            e.Property(w => w.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(w => w.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(w => w.PartnerId).IsUnique();
            e.HasOne(w => w.Partner).WithOne(p => p.Wallet!).HasForeignKey<Wallet>(w => w.PartnerId);
        });

        // Wallet ledger
        b.Entity<WalletLedger>(e =>
        {
            e.ToTable("wallet_ledger");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(l => l.WalletId).HasColumnName("wallet_id");
            e.Property(l => l.TopupId).HasColumnName("topup_id").HasDefaultValue(0);
            e.Property(l => l.EntryType).HasColumnName("entry_type").HasConversion<int>();
            e.Property(l => l.Reason).HasColumnName("reason").HasConversion<int>();
            e.Property(l => l.AmountFils).HasColumnName("amount_fils");
            e.Property(l => l.RunningBalanceFils).HasColumnName("running_balance_fils");
            e.Property(l => l.ReasonText).HasColumnName("reason_text").HasMaxLength(255);
            e.Property(l => l.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(l => l.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(l => new { l.WalletId, l.CreatedAtUtc, l.Id });
            e.HasIndex(l => l.TopupId);
        });

        // Topups
        b.Entity<Topup>(e =>
        {
            e.ToTable("topups");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(t => t.PartnerId).HasColumnName("partner_id");
            e.Property(t => t.WalletId).HasColumnName("wallet_id");
            e.Property(t => t.ClientRef).HasColumnName("client_ref").HasMaxLength(64).IsRequired();
            e.Property(t => t.MsisdnNormalized).HasColumnName("msisdn_normalized").HasMaxLength(20).IsRequired();
            e.Property(t => t.MsisdnMasked).HasColumnName("msisdn_masked").HasMaxLength(20).IsRequired();
            e.Property(t => t.Operator).HasColumnName("operator").HasConversion<int>();
            e.Property(t => t.FaceValueFils).HasColumnName("face_value_fils");
            e.Property(t => t.RetailerCommissionFils).HasColumnName("retailer_commission_fils");
            e.Property(t => t.DistributorShareFils).HasColumnName("distributor_share_fils");
            e.Property(t => t.WalletDebitFils).HasColumnName("wallet_debit_fils");
            e.Property(t => t.Status).HasColumnName("status").HasConversion<int>();
            e.Property(t => t.OperatorTxnId).HasColumnName("operator_txn_id").HasMaxLength(64);
            e.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(t => t.BusinessDateUtc).HasColumnName("business_date_utc");
            e.Property(t => t.FinalStateAtUtc).HasColumnName("final_state_at_utc");
            e.Property(t => t.CallbackReceivedAtUtc).HasColumnName("callback_received_at_utc");
            e.HasIndex(t => new { t.PartnerId, t.ClientRef }).IsUnique();
            e.HasIndex(t => new { t.PartnerId, t.Status, t.CreatedAtUtc, t.Id });
            e.HasIndex(t => new { t.BusinessDateUtc, t.Status });
            e.HasIndex(t => new { t.Operator, t.Status, t.CreatedAtUtc });
            e.HasIndex(t => t.MsisdnNormalized);
            e.HasIndex(t => t.OperatorTxnId);
            e.HasIndex(t => new { t.CreatedAtUtc, t.Id });
        });

        // Commission rules
        b.Entity<CommissionRule>(e =>
        {
            e.ToTable("commission_rules");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(r => r.FaceValueFromFils).HasColumnName("face_value_from_fils");
            e.Property(r => r.FaceValueToFils).HasColumnName("face_value_to_fils");
            e.Property(r => r.RetailerRatePct).HasColumnName("retailer_rate_pct").HasColumnType("DECIMAL(6,3)");
            e.Property(r => r.DistributorRatePct).HasColumnName("distributor_rate_pct").HasColumnType("DECIMAL(6,3)");
            e.Property(r => r.EffectiveFromUtc).HasColumnName("effective_from_utc");
            e.Property(r => r.EffectiveToUtc).HasColumnName("effective_to_utc");
            e.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        // Idempotency records
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(i => i.PartnerId).HasColumnName("partner_id");
            e.Property(i => i.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(80).IsRequired();
            e.Property(i => i.RequestBodySha256).HasColumnName("request_body_sha256").HasMaxLength(64).IsRequired();
            e.Property(i => i.ClientRef).HasColumnName("client_ref").HasMaxLength(64);
            e.Property(i => i.Status).HasColumnName("status").HasConversion<int>();
            e.Property(i => i.ResponseStatus).HasColumnName("response_status");
            e.Property(i => i.ResponseBodyJson).HasColumnName("response_body_json");
            e.Property(i => i.ResourceLocation).HasColumnName("resource_location").HasMaxLength(255);
            e.Property(i => i.TopupId).HasColumnName("topup_id");
            e.Property(i => i.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(i => i.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(i => i.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.HasIndex(i => new { i.PartnerId, i.IdempotencyKey }).IsUnique();
            e.HasIndex(i => i.ExpiresAtUtc);
            e.HasIndex(i => new { i.PartnerId, i.ClientRef });
        });

        // Callback history
        b.Entity<CallbackHistory>(e =>
        {
            e.ToTable("callback_history");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.TopupId).HasColumnName("topup_id");
            e.Property(c => c.Operator).HasColumnName("operator").HasConversion<int>();
            e.Property(c => c.OperatorTxnId).HasColumnName("operator_txn_id").HasMaxLength(64);
            e.Property(c => c.ReportedStatus).HasColumnName("reported_status").HasConversion<int>();
            e.Property(c => c.Outcome).HasColumnName("outcome").HasConversion<int>();
            e.Property(c => c.ReceivedAtUtc).HasColumnName("received_at_utc");
            e.Property(c => c.RawBody).HasColumnName("raw_body");
            e.HasIndex(c => new { c.TopupId, c.ReceivedAtUtc });
            e.HasIndex(c => c.OperatorTxnId);
        });

        // Settlements
        b.Entity<Settlement>(e =>
        {
            e.ToTable("settlements");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(s => s.PartnerId).HasColumnName("partner_id");
            e.Property(s => s.BusinessDateUtc).HasColumnName("business_date_utc");
            e.Property(s => s.SuccessCount).HasColumnName("success_count");
            e.Property(s => s.FailedCount).HasColumnName("failed_count");
            e.Property(s => s.ExpiredCount).HasColumnName("expired_count");
            e.Property(s => s.PendingCount).HasColumnName("pending_count");
            e.Property(s => s.TotalFaceValueFils).HasColumnName("total_face_value_fils");
            e.Property(s => s.TotalRetailerCommissionFils).HasColumnName("total_retailer_commission_fils");
            e.Property(s => s.TotalDistributorShareFils).HasColumnName("total_distributor_share_fils");
            e.Property(s => s.NetDebitFils).HasColumnName("net_debit_fils");
            e.Property(s => s.GeneratedAtUtc).HasColumnName("generated_at_utc");
            e.HasIndex(s => new { s.PartnerId, s.BusinessDateUtc }).IsUnique();
        });

        b.Entity<SettlementItem>(e =>
        {
            e.ToTable("settlement_items");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(i => i.SettlementId).HasColumnName("settlement_id");
            e.Property(i => i.TopupId).HasColumnName("topup_id");
            e.Property(i => i.FaceValueFils).HasColumnName("face_value_fils");
            e.Property(i => i.RetailerCommissionFils).HasColumnName("retailer_commission_fils");
            e.Property(i => i.DistributorShareFils).HasColumnName("distributor_share_fils");
            e.Property(i => i.Status).HasColumnName("status").HasConversion<int>();
            e.Property(i => i.Operator).HasColumnName("operator").HasConversion<int>();
            e.HasIndex(i => new { i.SettlementId, i.TopupId }).IsUnique();
        });

        // Reconciliation
        b.Entity<OperatorReconciliationFile>(e =>
        {
            e.ToTable("operator_reconciliation_files");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(f => f.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
            e.Property(f => f.FileSha256).HasColumnName("file_sha256").HasMaxLength(64).IsRequired();
            e.Property(f => f.Operator).HasColumnName("operator").HasConversion<int>();
            e.Property(f => f.BusinessDateUtc).HasColumnName("business_date_utc");
            e.Property(f => f.UploadedAtUtc).HasColumnName("uploaded_at_utc");
            e.Property(f => f.TotalRows).HasColumnName("total_rows");
            e.Property(f => f.MatchedCount).HasColumnName("matched_count");
            e.Property(f => f.MissingInOursCount).HasColumnName("missing_in_ours_count");
            e.Property(f => f.MissingInOperatorCount).HasColumnName("missing_in_operator_count");
            e.Property(f => f.AmountMismatchCount).HasColumnName("amount_mismatch_count");
            e.Property(f => f.StatusMismatchCount).HasColumnName("status_mismatch_count");
            e.HasIndex(f => new { f.FileName, f.FileSha256, f.Operator }).IsUnique();
        });

        b.Entity<OperatorReconciliationRow>(e =>
        {
            e.ToTable("operator_reconciliation_rows");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(r => r.FileId).HasColumnName("file_id");
            e.Property(r => r.OperatorTxnId).HasColumnName("operator_txn_id").HasMaxLength(64).IsRequired();
            e.Property(r => r.Msisdn).HasColumnName("msisdn").HasMaxLength(20).IsRequired();
            e.Property(r => r.AmountFils).HasColumnName("amount_fils");
            e.Property(r => r.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            e.Property(r => r.EventTimeDubai).HasColumnName("event_time_dubai");
            e.Property(r => r.MatchedTopupId).HasColumnName("matched_topup_id");
            e.Property(r => r.Match).HasColumnName("match").HasConversion<int>();
            e.Property(r => r.Note).HasColumnName("note").HasMaxLength(255);
            e.HasIndex(r => new { r.FileId, r.OperatorTxnId }).IsUnique();
        });

        // Admin users
        b.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(u => u.Username).HasColumnName("username").HasMaxLength(64).IsRequired();
            e.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(120).IsRequired();
            e.Property(u => u.Role).HasColumnName("role").HasConversion<int>();
            e.Property(u => u.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(u => u.LastLoginAtUtc).HasColumnName("last_login_at_utc");
            e.HasIndex(u => u.Username).IsUnique();
        });
    }
}
