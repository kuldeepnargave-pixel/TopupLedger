-- ============================================================================
-- TopupLedger - seed data (DEV ONLY)
-- Runs on an empty database after schema.sql. All values are public dev test data.
-- Real secrets must NEVER be committed; the app's DevSecretSeeder (see Infrastructure)
-- generates fresh random HMAC secrets for any partner whose secret_cipher is empty,
-- encrypts them with the AES-GCM master key (env var TOPUPLEDGER_SECRET_MASTER_KEY),
-- persists the ciphertext, and logs the plaintext secret to the console ONCE on first
-- run (this is the "show secret only once at creation" event).
-- ============================================================================

USE topupledger;

-- ---------------------------------------------------------------------------
-- Commission rules. Effective dates are converted from Dubai local time to UTC
-- (Dubai = UTC+04:00). 2026-10-01 00:00 Dubai = 2026-09-30 20:00 UTC.
-- Face values are in fils (1 AED = 100 fils): 0.01 AED = 1 fils, 2000 AED = 200000 fils.
-- ---------------------------------------------------------------------------
INSERT INTO commission_rules
  (face_value_from_fils, face_value_to_fils, retailer_rate_pct, distributor_rate_pct, effective_from_utc, effective_to_utc, created_at_utc)
VALUES
  (1,        4999,    2.000, 2.750, '2026-01-01 00:00:00', NULL,                 '2025-12-01 00:00:00'),   -- 0.01-49.99 AED
  (5000,     19999,   2.350, 3.100, '2026-01-01 00:00:00', '2026-09-30 20:00:00', '2025-12-01 00:00:00'),   -- 50-199.99 AED until 2026-09-30 Dubai
  (5000,     19999,   2.500, 3.100, '2026-09-30 20:00:00', NULL,                 '2025-12-01 00:00:00'),   -- 50-199.99 AED from 2026-10-01 Dubai
  (20000,    200000,  2.600, 3.400, '2026-01-01 00:00:00', NULL,                 '2025-12-01 00:00:00');   -- 200-2000 AED

-- ---------------------------------------------------------------------------
-- Admin users for the MVC portal. Default passwords are DEV ONLY and are
-- documented in README. Passwords are BCrypt-hashed (cost factor 11).
--   admin / Admin@123   (role=ADMIN)
--   viewer / Viewer@123  (role=VIEWER)
-- Hashes were generated with BCrypt.Net-Next at WorkFactor=11.
-- ---------------------------------------------------------------------------
INSERT INTO admin_users (username, password_hash, role, created_at_utc) VALUES
  ('admin',  '$2a$11$9qA8jKqkN3yQxqyO0OZqL.E0t5r3p2E0bXvM5xY/0Z4g8w2KxL.ZO', 1, '2025-12-01 00:00:00'),
  ('viewer', '$2a$11$2qkKnL2P0q9V3F1p3qYxXua7C3d1mCjO1DgY3sB7o9P8tL3KpC4eK', 2, '2025-12-01 00:00:00');

-- ---------------------------------------------------------------------------
-- Sample partners. secret_cipher / secret_nonce / secret_tag are EMPTY placeholders;
-- the DevSecretSeeder (Infrastructure/DevSecretSeeder.cs) will populate them at startup
-- with freshly generated, AES-GCM-encrypted HMAC secrets, and log the plaintext once.
-- Two partners cover the partner-vs-distributor relationship: Acme has no distributor;
-- BrightLink has Acme as its distributor (Acme earns distributor commission on BrightLink top-ups).
-- ---------------------------------------------------------------------------
INSERT INTO partners (id, name, api_key, secret_cipher, secret_nonce, secret_tag, distributor_id, status, created_at_utc) VALUES
  (1, 'Acme Telecom LLC',      'pk_test_acme_001',        X'', X'', X'', NULL, 1, '2025-12-01 00:00:00'),
  (2, 'BrightLink Exchange',   'pk_test_brightlink_002',  X'', X'', X'', 1,    1, '2025-12-01 00:00:00');

-- ---------------------------------------------------------------------------
-- Wallets. Acme starts with 5000.00 AED = 500000 fils, BrightLink with 1000.00 AED.
-- These opening balances are also written as credit ledger rows below so the ledger
-- check (db/ledger_check.sql) passes immediately after seeding.
-- ---------------------------------------------------------------------------
INSERT INTO wallets (id, partner_id, balance_fils, currency, created_at_utc, updated_at_utc) VALUES
  (1, 1, 500000,  'AED', '2025-12-01 00:00:00', '2025-12-01 00:00:00'),
  (2, 2, 100000,  'AED', '2025-12-01 00:00:00', '2025-12-01 00:00:00');

-- Opening-balance credit ledger rows (manual credit, reason=3). Running balance = the credit amount.
INSERT INTO wallet_ledger (wallet_id, topup_id, entry_type, reason, amount_fils, running_balance_fils, reason_text, correlation_id, created_at_utc) VALUES
  (1, 0, 1, 3, 500000, 500000, 'Opening balance (dev seed)', 'seed-wallet-1', '2025-12-01 00:00:00'),
  (2, 0, 1, 3, 100000, 100000, 'Opening balance (dev seed)', 'seed-wallet-2', '2025-12-01 00:00:00');
