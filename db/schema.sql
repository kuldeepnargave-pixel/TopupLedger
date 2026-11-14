-- ============================================================================
-- TopupLedger - MySQL 8 schema
-- InnoDB engine, utf8mb4 character set.
-- Money is stored as BIGINT fils (1 AED = 100 fils). Never DECIMAL for money.
-- All timestamps are stored in UTC and converted at the application layer using
-- Asia/Dubai (UTC+04:00) for business-day calculations.
-- ============================================================================

CREATE DATABASE IF NOT EXISTS topupledger
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_unicode_ci;
USE topupledger;

SET FOREIGN_KEY_CHECKS = 0;
DROP TABLE IF EXISTS operator_reconciliation_rows;
DROP TABLE IF EXISTS operator_reconciliation_files;
DROP TABLE IF EXISTS settlement_items;
DROP TABLE IF EXISTS settlements;
DROP TABLE IF EXISTS callback_history;
DROP TABLE IF EXISTS wallet_ledger;
DROP TABLE IF EXISTS topups;
DROP TABLE IF EXISTS idempotency_records;
DROP TABLE IF EXISTS wallets;
DROP TABLE IF EXISTS commission_rules;
DROP TABLE IF EXISTS admin_users;
DROP TABLE IF EXISTS partners;
SET FOREIGN_KEY_CHECKS = 1;

-- ---------------------------------------------------------------------------
-- Partners. Self-reference allows a partner to have a parent distributor.
-- secret_cipher / secret_nonce / secret_tag together hold the AES-GCM
-- encrypted HMAC secret (never plain text). See DECISIONS.md.
-- ---------------------------------------------------------------------------
CREATE TABLE partners (
  id              BIGINT       NOT NULL AUTO_INCREMENT,
  name            VARCHAR(120) NOT NULL,
  api_key         VARCHAR(64)  NOT NULL,
  secret_cipher   VARBINARY(512) NOT NULL,
  secret_nonce    VARBINARY(24)  NOT NULL,
  secret_tag      VARBINARY(16)  NOT NULL,
  distributor_id  BIGINT       NULL,
  status          TINYINT      NOT NULL DEFAULT 1 COMMENT '1=Active, 2=Suspended',
  created_at_utc  DATETIME     NOT NULL,
  suspended_at_utc DATETIME    NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_partners_api_key (api_key),
  KEY idx_partners_distributor (distributor_id),
  CONSTRAINT fk_partners_distributor FOREIGN KEY (distributor_id)
    REFERENCES partners(id) ON DELETE RESTRICT ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Wallets. balance_fils is a denormalised cache of SUM(ledger.amount_fils).
-- Always updated inside the same transaction as the ledger writes and protected
-- by SELECT ... FOR UPDATE.
-- ---------------------------------------------------------------------------
CREATE TABLE wallets (
  id            BIGINT  NOT NULL AUTO_INCREMENT,
  partner_id    BIGINT  NOT NULL,
  balance_fils  BIGINT  NOT NULL DEFAULT 0,
  currency      VARCHAR(3) NOT NULL DEFAULT 'AED',
  created_at_utc DATETIME NOT NULL,
  updated_at_utc DATETIME NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_wallets_partner (partner_id),
  CONSTRAINT fk_wallets_partner FOREIGN KEY (partner_id)
    REFERENCES partners(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Wallet ledger (append-only, double-entry). NEVER UPDATE or DELETE rows.
-- A refund creates a new compensating row with entry_type=CREDIT and reason=TopupRefund.
-- ---------------------------------------------------------------------------
CREATE TABLE wallet_ledger (
  id                   BIGINT   NOT NULL AUTO_INCREMENT,
  wallet_id            BIGINT   NOT NULL,
  topup_id             BIGINT   NOT NULL DEFAULT 0 COMMENT '0 for manual credits',
  entry_type           TINYINT  NOT NULL COMMENT '1=Credit, 2=Debit',
  reason               TINYINT  NOT NULL,
  amount_fils          BIGINT   NOT NULL,
  running_balance_fils BIGINT   NOT NULL COMMENT 'wallet balance immediately after this row',
  reason_text          VARCHAR(255) NULL,
  correlation_id       VARCHAR(64)  NULL,
  created_at_utc       DATETIME NOT NULL,
  PRIMARY KEY (id),
  KEY idx_ledger_wallet_time (wallet_id, created_at_utc, id),
  KEY idx_ledger_topup (topup_id),
  KEY idx_ledger_correlation (correlation_id),
  CONSTRAINT fk_ledger_wallet FOREIGN KEY (wallet_id)
    REFERENCES wallets(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Commission rules. effective_from_utc is inclusive; effective_to_utc is exclusive
-- and NULL means open-ended. The assignment table:
--   0.01-49.99   2026-01-01  2.00%  2.75%
--   50-199.99    2026-01-01  2.35%  3.10%  (retailer rate until 2026-09-30 23:59:59 Dubai)
--   50-199.99    2026-10-01  2.50%  3.10%  (new retailer rate)
--   200-2000     2026-01-01  2.60%  3.40%
-- Dubai time = UTC+04:00, so 2026-10-01 00:00:00 Dubai = 2026-09-30 20:00:00 UTC.
-- ---------------------------------------------------------------------------
CREATE TABLE commission_rules (
  id                       BIGINT   NOT NULL AUTO_INCREMENT,
  face_value_from_fils     BIGINT   NOT NULL,
  face_value_to_fils       BIGINT   NOT NULL,
  retailer_rate_pct        DECIMAL(6,3) NOT NULL,
  distributor_rate_pct     DECIMAL(6,3) NOT NULL,
  effective_from_utc       DATETIME NOT NULL,
  effective_to_utc         DATETIME NULL,
  created_at_utc           DATETIME NOT NULL,
  PRIMARY KEY (id),
  KEY idx_commission_face_range (face_value_from_fils, face_value_to_fils, effective_from_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Topups (transactions). Status: 1=Pending, 2=Success, 3=Failed, 4=Expired.
-- All money columns are BIGINT fils.
-- business_date_utc is midnight UTC of the Dubai business day (post cut-off shift).
-- ---------------------------------------------------------------------------
CREATE TABLE topups (
  id                          BIGINT   NOT NULL AUTO_INCREMENT,
  partner_id                  BIGINT   NOT NULL,
  wallet_id                   BIGINT   NOT NULL,
  client_ref                  VARCHAR(64)  NOT NULL,
  msisdn_normalized           VARCHAR(20)  NOT NULL,
  msisdn_masked               VARCHAR(20)  NOT NULL,
  operator                    TINYINT  NOT NULL COMMENT '1=Etisalat, 2=Du',
  face_value_fils             BIGINT   NOT NULL,
  retailer_commission_fils    BIGINT   NOT NULL,
  distributor_share_fils      BIGINT   NOT NULL,
  wallet_debit_fils           BIGINT   NOT NULL,
  status                      TINYINT  NOT NULL DEFAULT 1,
  operator_txn_id             VARCHAR(64)  NULL,
  created_at_utc              DATETIME NOT NULL,
  business_date_utc           DATETIME NOT NULL,
  final_state_at_utc         DATETIME NULL,
  callback_received_at_utc    DATETIME NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_topups_partner_clientref (partner_id, client_ref),
  KEY idx_topups_partner_status_created (partner_id, status, created_at_utc, id),
  KEY idx_topups_business_date (business_date_utc, status),
  KEY idx_topups_operator_status (operator, status, created_at_utc),
  KEY idx_topups_msisdn (msisdn_normalized, created_at_utc),
  KEY idx_topups_operator_txn (operator_txn_id),
  KEY idx_topups_created_id (created_at_utc, id) COMMENT 'keyset pagination on time+id',
  CONSTRAINT fk_topups_partner FOREIGN KEY (partner_id) REFERENCES partners(id),
  CONSTRAINT fk_topups_wallet  FOREIGN KEY (wallet_id)  REFERENCES wallets(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Idempotency records. Same partner + same key + same body hash = replay.
-- Same key, different body hash = 422 IDEMPOTENCY_KEY_REUSED.
-- Status 1=InProgress, 2=Completed. Expires_at_utc = created_at + 24h.
-- Different partners may use the same key value independently -> unique key
-- is (partner_id, idempotency_key) NOT idempotency_key alone.
-- ---------------------------------------------------------------------------
CREATE TABLE idempotency_records (
  id                    BIGINT  NOT NULL AUTO_INCREMENT,
  partner_id            BIGINT  NOT NULL,
  idempotency_key       VARCHAR(80) NOT NULL,
  request_body_sha256   CHAR(64)    NOT NULL,
  client_ref            VARCHAR(64) NULL,
  status                TINYINT  NOT NULL COMMENT '1=InProgress, 2=Completed',
  response_status       INT      NOT NULL DEFAULT 0,
  response_body_json    MEDIUMTEXT NULL,
  resource_location     VARCHAR(255) NULL,
  topup_id              BIGINT  NULL,
  created_at_utc        DATETIME NOT NULL,
  completed_at_utc      DATETIME NULL,
  expires_at_utc       DATETIME NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_idem_partner_key (partner_id, idempotency_key),
  KEY idx_idem_expires (expires_at_utc),
  KEY idx_idem_partner_clientref (partner_id, client_ref)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Callback history. Records every callback, including duplicates and late arrivals.
-- ---------------------------------------------------------------------------
CREATE TABLE callback_history (
  id                BIGINT   NOT NULL AUTO_INCREMENT,
  topup_id          BIGINT   NOT NULL,
  operator          TINYINT  NOT NULL,
  operator_txn_id   VARCHAR(64)  NULL,
  reported_status   TINYINT  NOT NULL,
  outcome           TINYINT  NOT NULL COMMENT '1=Received,2=Duplicate,3=IgnoredAfterTerminal',
  received_at_utc   DATETIME NOT NULL,
  raw_body          TEXT     NULL,
  PRIMARY KEY (id),
  KEY idx_callback_topup (topup_id, received_at_utc),
  KEY idx_callback_operator_txn (operator_txn_id),
  CONSTRAINT fk_callback_topup FOREIGN KEY (topup_id) REFERENCES topups(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Settlements. Generated once per (partner, businessDay); unique key prevents duplicates.
-- SettlementItems captures the individual top-ups aggregated in the settlement.
-- ---------------------------------------------------------------------------
CREATE TABLE settlements (
  id                            BIGINT   NOT NULL AUTO_INCREMENT,
  partner_id                    BIGINT   NOT NULL,
  business_date_utc             DATETIME NOT NULL,
  success_count                 BIGINT   NOT NULL DEFAULT 0,
  failed_count                  BIGINT   NOT NULL DEFAULT 0,
  expired_count                 BIGINT   NOT NULL DEFAULT 0,
  pending_count                 BIGINT   NOT NULL DEFAULT 0,
  total_face_value_fils         BIGINT   NOT NULL DEFAULT 0,
  total_retailer_commission_fils BIGINT  NOT NULL DEFAULT 0,
  total_distributor_share_fils  BIGINT   NOT NULL DEFAULT 0,
  net_debit_fils                BIGINT   NOT NULL DEFAULT 0,
  generated_at_utc              DATETIME NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_settlements_partner_day (partner_id, business_date_utc),
  KEY idx_settlements_day (business_date_utc),
  CONSTRAINT fk_settlements_partner FOREIGN KEY (partner_id) REFERENCES partners(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE settlement_items (
  id                          BIGINT  NOT NULL AUTO_INCREMENT,
  settlement_id               BIGINT  NOT NULL,
  topup_id                    BIGINT  NOT NULL,
  face_value_fils             BIGINT  NOT NULL,
  retailer_commission_fils    BIGINT  NOT NULL,
  distributor_share_fils      BIGINT  NOT NULL,
  status                      TINYINT NOT NULL,
  operator                    TINYINT NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_settlement_items (settlement_id, topup_id),
  KEY idx_settlement_items_topup (topup_id),
  CONSTRAINT fk_si_settlement FOREIGN KEY (settlement_id) REFERENCES settlements(id) ON DELETE CASCADE,
  CONSTRAINT fk_si_topup      FOREIGN KEY (topup_id)      REFERENCES topups(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Operator reconciliation files + rows. Dedup by (file_name, file_sha256, operator).
-- ---------------------------------------------------------------------------
CREATE TABLE operator_reconciliation_files (
  id                    BIGINT   NOT NULL AUTO_INCREMENT,
  file_name             VARCHAR(255) NOT NULL,
  file_sha256           CHAR(64)     NOT NULL,
  operator              TINYINT  NOT NULL,
  business_date_utc     DATETIME NULL,
  uploaded_at_utc       DATETIME NOT NULL,
  total_rows            BIGINT   NOT NULL DEFAULT 0,
  matched_count        BIGINT   NOT NULL DEFAULT 0,
  missing_in_ours_count        BIGINT NOT NULL DEFAULT 0,
  missing_in_operator_count    BIGINT NOT NULL DEFAULT 0,
  amount_mismatch_count        BIGINT NOT NULL DEFAULT 0,
  status_mismatch_count        BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (id),
  UNIQUE KEY uq_recon_file_dedup (file_name, file_sha256, operator),
  KEY idx_recon_file_uploaded (uploaded_at_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE operator_reconciliation_rows (
  id                BIGINT   NOT NULL AUTO_INCREMENT,
  file_id           BIGINT   NOT NULL,
  operator_txn_id   VARCHAR(64)  NOT NULL,
  msisdn            VARCHAR(20)  NOT NULL,
  amount_fils       BIGINT   NOT NULL,
  status            VARCHAR(20)  NOT NULL,
  event_time_dubai  DATETIME NOT NULL,
  matched_topup_id  BIGINT   NULL,
  match             TINYINT  NOT NULL COMMENT '1=Matched,2=MissingInOurs,3=MissingInOperator,4=AmountMismatch,5=StatusMismatch',
  note              VARCHAR(255) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_recon_rows (file_id, operator_txn_id),
  KEY idx_recon_rows_match (file_id, match),
  KEY idx_recon_rows_topup (matched_topup_id),
  CONSTRAINT fk_recon_rows_file FOREIGN KEY (file_id)
    REFERENCES operator_reconciliation_files(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Admin users (MVC portal). Passwords stored BCrypt-hashed.
-- ---------------------------------------------------------------------------
CREATE TABLE admin_users (
  id                BIGINT   NOT NULL AUTO_INCREMENT,
  username          VARCHAR(64)  NOT NULL,
  password_hash     VARCHAR(120) NOT NULL COMMENT 'BCrypt hash',
  role              TINYINT  NOT NULL COMMENT '1=Admin, 2=Viewer',
  created_at_utc    DATETIME NOT NULL,
  last_login_at_utc DATETIME NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_admin_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
