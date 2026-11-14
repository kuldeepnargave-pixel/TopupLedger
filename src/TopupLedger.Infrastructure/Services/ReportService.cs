
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.DTOs;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Configuration;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Infrastructure.Services;

/// <summary>
/// Implements all 10 read-only report APIs. SQL aggregations run inside MySQL.
/// MSISDNs are masked in every result.
/// Reports are read-only: no row locks are taken.
///
/// Business-day semantics: all dates are interpreted in Dubai time (UTC+04:00)
/// with a 22:00 cut-off.
/// </summary>
public sealed class ReportService
{
    private readonly TopupLedgerDbContext _db;
    private readonly BusinessDayService _businessDays;
    private readonly ILogger<ReportService> _logger;

    public ReportService(
        TopupLedgerDbContext db,
        BusinessDayService businessDays,
        ILogger<ReportService> logger)
    {
        _db = db;
        _businessDays = businessDays;
        _logger = logger;
    }

    // =====================================================================
    // Date range validation
    // =====================================================================
    public (DateTime fromUtc, DateTime toUtc) ResolveRange(
        string? from,
        string? to,
        int maxDays = 31)
    {
        DateTime f, t;

        if (string.IsNullOrEmpty(from) && string.IsNullOrEmpty(to))
        {
            (f, t) = _businessDays.DefaultRange(7);
        }
        else
        {
            f = string.IsNullOrEmpty(from)
                ? _businessDays.GetBusinessDayUtc(DateTime.UtcNow).AddDays(-6)
                : _businessDays.ParseBusinessDay(from);

            t = string.IsNullOrEmpty(to)
                ? _businessDays.GetBusinessDayUtc(DateTime.UtcNow)
                : _businessDays.ParseBusinessDay(to);
        }

        if ((t - f).TotalDays > maxDays)
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                $"Date range must not exceed {maxDays} business days. Got {(t - f).TotalDays + 1} days.");
        }

        return (f, t.AddDays(1));
    }

    // =====================================================================
    // Report 1: Daily summary
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> DailySummaryAsync(
        string? from,
        string? to,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = $@"
SELECT
  t.business_date_utc AS business_date,
  COUNT(*) AS count_total,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN 1 ELSE 0 END) AS count_success,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Failed} THEN 1 ELSE 0 END) AS count_failed,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Expired} THEN 1 ELSE 0 END) AS count_expired,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Pending} THEN 1 ELSE 0 END) AS count_pending,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN t.face_value_fils ELSE 0 END) AS face_value_success_fils,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Failed} THEN t.face_value_fils ELSE 0 END) AS face_value_failed_fils,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Expired} THEN t.face_value_fils ELSE 0 END) AS face_value_expired_fils,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Pending} THEN t.face_value_fils ELSE 0 END) AS face_value_pending_fils,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN t.retailer_commission_fils ELSE 0 END) AS retailer_commission_fils,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN t.distributor_share_fils ELSE 0 END) AS distributor_share_fils
FROM topups t
WHERE t.business_date_utc >= @from AND t.business_date_utc < @to
GROUP BY t.business_date_utc
ORDER BY t.business_date_utc;";

        var rows = new List<Dictionary<string, object>>();

        var conn = _db.Database.GetDbConnection();
        bool closeConnection = conn.State != System.Data.ConnectionState.Open;

        try
        {
            if (closeConnection)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object>();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i);
                }

                rows.Add(row);
            }
        }
        finally
        {
            if (closeConnection)
                await conn.CloseAsync();
        }

        var totalRow = new Dictionary<string, object>
        {
            ["business_date"] = "GRAND_TOTAL",
            ["count_total"] = rows.Sum(r => Convert.ToInt64(r["count_total"])),
            ["count_success"] = rows.Sum(r => Convert.ToInt64(r["count_success"])),
            ["count_failed"] = rows.Sum(r => Convert.ToInt64(r["count_failed"])),
            ["count_expired"] = rows.Sum(r => Convert.ToInt64(r["count_expired"])),
            ["count_pending"] = rows.Sum(r => Convert.ToInt64(r["count_pending"])),
            ["face_value_success_fils"] = rows.Sum(r => Convert.ToInt64(r["face_value_success_fils"])),
            ["face_value_failed_fils"] = rows.Sum(r => Convert.ToInt64(r["face_value_failed_fils"])),
            ["face_value_expired_fils"] = rows.Sum(r => Convert.ToInt64(r["face_value_expired_fils"])),
            ["face_value_pending_fils"] = rows.Sum(r => Convert.ToInt64(r["face_value_pending_fils"])),
            ["retailer_commission_fils"] = rows.Sum(r => Convert.ToInt64(r["retailer_commission_fils"])),
            ["distributor_share_fils"] = rows.Sum(r => Convert.ToInt64(r["distributor_share_fils"]))
        };

        rows.Add(totalRow);

        return rows;
    }

    // =====================================================================
    // Report 2: Operators
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> OperatorsAsync(
        string? from,
        string? to,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = $@"
SELECT
  t.operator AS operator,
  COUNT(*) AS count_total,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN 1 ELSE 0 END) AS count_success,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Failed} THEN 1 ELSE 0 END) AS count_failed,
  SUM(CASE WHEN t.status = {(int)TopupStatus.Expired} THEN 1 ELSE 0 END) AS count_expired,
  ROUND(100.0 * SUM(CASE WHEN t.status = {(int)TopupStatus.Success} THEN 1 ELSE 0 END) / COUNT(*), 2) AS success_pct,
  AVG(CASE WHEN t.status IN ({(int)TopupStatus.Success},{(int)TopupStatus.Failed}) AND t.final_state_at_utc IS NOT NULL
           THEN TIMESTAMPDIFF(SECOND, t.created_at_utc, t.final_state_at_utc) END) AS avg_callback_seconds
FROM topups t
WHERE t.business_date_utc >= @from AND t.business_date_utc < @to
GROUP BY t.operator
ORDER BY t.operator;";

        return await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
        }, ct);
    }

    // =====================================================================
    // Report 3: Partners top (ranked by SUCCESS face value)
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> PartnersTopAsync(
        string? from,
        string? to,
        int limit,
        CancellationToken ct)
    {
        if (limit <= 0 || limit > 100)
            limit = 10;

        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = @"
SELECT ranked.*
FROM (
    SELECT
        p.id AS partner_id,
        p.name AS partner_name,
        SUM(t.face_value_fils) AS success_face_value_fils,
        SUM(t.retailer_commission_fils) AS retailer_commission_fils,
        SUM(t.distributor_share_fils) AS distributor_share_fils,
        COUNT(*) AS success_count,
        RANK() OVER (
            ORDER BY SUM(t.face_value_fils) DESC
        ) AS partner_rank
    FROM topups t
    JOIN partners p ON p.id = t.partner_id
    WHERE t.status = 2
      AND t.business_date_utc >= @from
      AND t.business_date_utc < @to
    GROUP BY p.id, p.name
) ranked
WHERE ranked.partner_rank <= @limit
ORDER BY ranked.partner_rank;";

        return await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
            AddParam(cmd, "@limit", limit);
        }, ct);
    }

    // =====================================================================
    // Report 4: Commission by partner per business day
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> CommissionAsync(
        string? from,
        string? to,
        long? partnerId,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = @"
SELECT
  t.partner_id AS partner_id,
  p.name AS partner_name,
  t.business_date_utc AS business_date,
  SUM(t.face_value_fils) AS face_value_fils,
  SUM(t.retailer_commission_fils) AS retailer_commission_fils,
  SUM(t.distributor_share_fils) AS distributor_share_fils,
  COUNT(*) AS success_count
FROM topups t
JOIN partners p ON p.id = t.partner_id
WHERE t.status = 2
  AND t.business_date_utc >= @from AND t.business_date_utc < @to
  AND (@partnerId IS NULL OR t.partner_id = @partnerId)
GROUP BY t.partner_id, p.name, t.business_date_utc
ORDER BY t.business_date_utc, t.partner_id;";

        return await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
            AddParam(cmd, "@partnerId", partnerId.HasValue ? partnerId.Value : DBNull.Value);
        }, ct);
    }

    // =====================================================================
    // Report 5: Wallet balances at a past instant
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> WalletsAsOfAsync(
        DateTime asOfUtc,
        CancellationToken ct)
    {
        var sql = @"
SELECT
  w.id AS wallet_id,
  w.partner_id,
  p.name AS partner_name,
  COALESCE(SUM(CASE WHEN l.entry_type = 1 THEN l.amount_fils WHEN l.entry_type = 2 THEN -l.amount_fils END), 0) AS balance_fils
FROM wallets w
JOIN partners p ON p.id = w.partner_id
LEFT JOIN wallet_ledger l ON l.wallet_id = w.id AND l.created_at_utc <= @asOf
GROUP BY w.id, w.partner_id, p.name
ORDER BY w.id;";

        return await ExecuteReaderAsync(
            sql,
            cmd => AddParam(cmd, "@asOf", asOfUtc),
            ct);
    }

    // =====================================================================
    // Report 6: Wallet statement
    // =====================================================================
    public async Task<Dictionary<string, object>> WalletStatementAsync(
        long partnerId,
        string? from,
        string? to,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var wallet = await _db.Wallets
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.PartnerId == partnerId, ct);

        if (wallet == null)
        {
            throw new DomainException(
                "NOT_FOUND",
                $"Wallet for partner {partnerId} not found.",
                httpStatusCode: 404);
        }

        var openingSql = @"
SELECT COALESCE(SUM(CASE WHEN entry_type = 1 THEN amount_fils WHEN entry_type = 2 THEN -amount_fils END), 0) AS opening_fils
FROM wallet_ledger
WHERE wallet_id = @walletId AND created_at_utc < @from;";

        long openingFils;

        var conn = _db.Database.GetDbConnection();
        bool closeConnection = conn.State != System.Data.ConnectionState.Open;

        try
        {
            if (closeConnection)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = openingSql;

            AddParam(cmd, "@walletId", wallet.Id);
            AddParam(cmd, "@from", fromUtc);

            var result = await cmd.ExecuteScalarAsync(ct);

            openingFils = result == null || result == DBNull.Value
                ? 0L
                : Convert.ToInt64(result);
        }
        finally
        {
            if (closeConnection)
                await conn.CloseAsync();
        }

        var entriesSql = @"
SELECT id, entry_type, reason, amount_fils, running_balance_fils, reason_text, created_at_utc
FROM wallet_ledger
WHERE wallet_id = @walletId AND created_at_utc >= @from AND created_at_utc < @to
ORDER BY id;";

        var entries = await ExecuteReaderAsync(entriesSql, cmd =>
        {
            AddParam(cmd, "@walletId", wallet.Id);
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
        }, ct);

        long closingFils = wallet.BalanceFils;

        return new Dictionary<string, object>
        {
            ["wallet_id"] = wallet.Id,
            ["partner_id"] = partnerId,
            ["from"] = fromUtc,
            ["to"] = toUtc,
            ["opening_balance_fils"] = openingFils,
            ["opening_balance_aed"] = MoneyFormatter.ToAed(openingFils),
            ["entries"] = entries,
            ["closing_balance_fils"] = closingFils,
            ["closing_balance_aed"] = MoneyFormatter.ToAed(closingFils)
        };
    }

    // =====================================================================
    // Report 7: Hourly
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> HourlyAsync(
        string businessDay,
        CancellationToken ct)
    {
        var businessDayUtc = _businessDays.ParseBusinessDay(businessDay);

        DateTime windowStartUtc = businessDayUtc.AddHours(-4);
        DateTime windowEndUtc = businessDayUtc.AddHours(20);

        var sql = @"
SELECT
  FLOOR(TIMESTAMPDIFF(MINUTE, @windowStart, t.created_at_utc) / 60) AS hour_index,
  COUNT(*) AS count_total,
  SUM(t.face_value_fils) AS face_value_fils
FROM topups t
WHERE t.created_at_utc >= @windowStart AND t.created_at_utc < @windowEnd
GROUP BY hour_index
ORDER BY hour_index;";

        var raw = await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@windowStart", windowStartUtc);
            AddParam(cmd, "@windowEnd", windowEndUtc);
        }, ct);

        var byIndex = raw
            .Where(r => r["hour_index"] != DBNull.Value)
            .ToDictionary(r => Convert.ToInt32(r["hour_index"]), r => r);

        var result = new List<Dictionary<string, object>>();

        for (int h = 0; h < 24; h++)
        {
            if (byIndex.TryGetValue(h, out var row))
            {
                result.Add(row);
            }
            else
            {
                result.Add(new Dictionary<string, object>
                {
                    ["hour_index"] = h,
                    ["count_total"] = 0L,
                    ["face_value_fils"] = 0L
                });
            }
        }

        return result;
    }

    // =====================================================================
    // Report 8: Failures
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> FailuresAsync(
        string? from,
        string? to,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = $@"
SELECT
  t.operator AS operator,
  t.status AS status,
  COUNT(*) AS count,
  SUM(t.wallet_debit_fils) AS debited_fils,
  COALESCE(SUM(refunds.refund_fils), 0) AS refunded_fils,
  CASE WHEN SUM(t.wallet_debit_fils) = COALESCE(SUM(refunds.refund_fils), 0) THEN 'OK' ELSE 'MISMATCH' END AS discrepancy
FROM topups t
LEFT JOIN (
  SELECT topup_id, SUM(amount_fils) AS refund_fils
  FROM wallet_ledger
  WHERE entry_type = 1 AND reason = {(int)LedgerReason.TopupRefund}
  GROUP BY topup_id
) refunds ON refunds.topup_id = t.id
WHERE t.status IN ({(int)TopupStatus.Failed}, {(int)TopupStatus.Expired})
  AND t.business_date_utc >= @from AND t.business_date_utc < @to
GROUP BY t.operator, t.status
ORDER BY t.operator, t.status;";

        return await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
        }, ct);
    }

    // =====================================================================
    // Report 9: Reconciliation files
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> ReconciliationAsync(
        string? from,
        string? to,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);

        var sql = @"
SELECT
  id AS file_id,
  file_name,
  operator,
  uploaded_at_utc AS upload_time,
  total_rows,
  matched_count,
  missing_in_ours_count,
  missing_in_operator_count,
  amount_mismatch_count,
  status_mismatch_count
FROM operator_reconciliation_files
WHERE uploaded_at_utc >= @from AND uploaded_at_utc < @to
ORDER BY uploaded_at_utc DESC;";

        return await ExecuteReaderAsync(sql, cmd =>
        {
            AddParam(cmd, "@from", fromUtc);
            AddParam(cmd, "@to", toUtc);
        }, ct);
    }

    // =====================================================================
    // Report 10: Partners inactive
    // =====================================================================
    public async Task<List<Dictionary<string, object>>> PartnersInactiveAsync(
        int days,
        CancellationToken ct)
    {
        if (days <= 0 || days > 365)
        {
            throw new DomainException(
                "VALIDATION_FAILED",
                "days must be 1..365.");
        }

        var cutoff = DateTime.UtcNow.AddDays(-days);

        var sql = @"
SELECT
  p.id AS partner_id,
  p.name AS partner_name,
  p.status AS partner_status,
  p.created_at_utc,
  MAX(CASE WHEN t.status = 2 THEN t.created_at_utc END) AS last_success_at_utc
FROM partners p
LEFT JOIN topups t ON t.partner_id = p.id AND t.status = 2 AND t.created_at_utc >= @cutoff
GROUP BY p.id, p.name, p.status, p.created_at_utc
HAVING last_success_at_utc IS NULL
ORDER BY p.id;";

        return await ExecuteReaderAsync(
            sql,
            cmd => AddParam(cmd, "@cutoff", cutoff),
            ct);
    }

    // =====================================================================
    // CSV Export
    // =====================================================================
    public static string ToCsv(
        IEnumerable<Dictionary<string, object>> rows,
        IEnumerable<string>? columns = null)
    {
        var sb = new StringBuilder();
        var list = rows.ToList();

        if (list.Count == 0)
            return string.Empty;

        var cols = columns?.ToList() ?? list[0].Keys.ToList();

        sb.AppendLine(string.Join(",", cols.Select(EscapeCsv)));

        foreach (var row in list)
        {
            sb.AppendLine(string.Join(
                ",",
                cols.Select(c => EscapeCSVValue(row.TryGetValue(c, out var v) ? v : null))));
        }

        return sb.ToString();
    }

    private static string EscapeCSVValue(object? v)
    {
        if (v == null || v == DBNull.Value)
            return "";

        string s = v switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => v.ToString() ?? ""
        };

        return EscapeCsv(s);
    }

    private static string EscapeCsv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        return s;
    }

    // =====================================================================
    // Shared SQL reader
    // =====================================================================
    private async Task<List<Dictionary<string, object>>> ExecuteReaderAsync(
        string sql,
        Action<DbCommand>? paramAction,
        CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object>>();

        var conn = _db.Database.GetDbConnection();
        bool closeConnection = conn.State != System.Data.ConnectionState.Open;

        try
        {
            if (closeConnection)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            paramAction?.Invoke(cmd);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object>();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] =
                        reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }
        finally
        {
            if (closeConnection)
                await conn.CloseAsync();
        }

        return rows;
    }

    // =====================================================================
    // SQL parameter helper
    // =====================================================================
    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
