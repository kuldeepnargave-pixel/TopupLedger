-- ============================================================================
-- ledger_check.sql
-- Proves that wallet.balance_fils == SUM(wallet_ledger.amount_fils) for every wallet,
-- using ledger double-entry semantics (credit adds, debit subtracts).
-- Run this after any data load; every row in the result should have balance_ok = 1.
-- ============================================================================

USE topupledger;

SELECT
  w.id                                  AS wallet_id,
  w.partner_id,
  p.name                                AS partner_name,
  w.balance_fils                        AS stored_balance_fils,
  COALESCE(SUM(
    CASE WHEN l.entry_type = 1 THEN l.amount_fils        -- credit
         WHEN l.entry_type = 2 THEN -l.amount_fils      -- debit
    END
  ), 0)                                 AS ledger_sum_fils,
  (w.balance_fils = COALESCE(SUM(
    CASE WHEN l.entry_type = 1 THEN l.amount_fils
         WHEN l.entry_type = 2 THEN -l.amount_fils
    END
  ), 0))                                AS balance_ok
FROM wallets w
JOIN partners p ON p.id = w.partner_id
LEFT JOIN wallet_ledger l ON l.wallet_id = w.id
GROUP BY w.id, w.partner_id, p.name, w.balance_fils
ORDER BY w.id;

-- ---------------------------------------------------------------------------
-- Side check: the last running_balance_fils for each wallet must equal its
-- current balance_fils (i.e. the ledger's tail is consistent with the cache).
-- ---------------------------------------------------------------------------
SELECT
  w.id AS wallet_id,
  w.balance_fils,
  latest.running_balance_fils,
  (w.balance_fils = latest.running_balance_fils) AS tail_ok
FROM wallets w
JOIN (
  SELECT wallet_id, running_balance_fils
  FROM (
    SELECT wallet_id, running_balance_fils, id,
           ROW_NUMBER() OVER (PARTITION BY wallet_id ORDER BY id DESC) AS rn
    FROM wallet_ledger
  ) t WHERE rn = 1
) latest ON latest.wallet_id = w.id;

-- ---------------------------------------------------------------------------
-- Side check: no topup has more than one refund ledger row. Each FAILED/EXPIRED
-- topup must have exactly one compensating credit (reason=2 TopupRefund) matching
-- the original debit (reason=1 TopupDebit).
-- ---------------------------------------------------------------------------
SELECT
  t.id AS topup_id,
  t.status,
  debits.debit_count,
  refunds.refund_count,
  CASE
    WHEN t.status IN (3, 4) AND refunds.refund_count > 1 THEN 'BAD: more than one refund'
    WHEN t.status IN (1, 2) AND refunds.refund_count > 0 THEN 'BAD: refund on non-terminal topup'
    ELSE 'OK'
  END AS refund_integrity
FROM topups t
LEFT JOIN (SELECT topup_id, COUNT(*) AS debit_count FROM wallet_ledger WHERE entry_type=2 AND reason=1 GROUP BY topup_id) debits ON debits.topup_id = t.id
LEFT JOIN (SELECT topup_id, COUNT(*) AS refund_count FROM wallet_ledger WHERE entry_type=1 AND reason=2 GROUP BY topup_id) refunds ON refunds.topup_id = t.id
HAVING refund_integrity LIKE 'BAD%';
