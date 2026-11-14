# DECISIONS.md

This document is intentionally short (max 2 pages). It records the non-trivial trade-offs
and any unresolved assignment ambiguity.

## 1. Architecture choices

Layered (Domain -> Application -> Infrastructure -> Api / Portal) with thin controllers. The
Application layer holds pure domain services (`CommissionService`, `WalletService`,
`TopupService`, `MsisdnService`, `BusinessDayService`, `HmacSignatureService`) that depend
only on repository interfaces. Services that need direct DbContext access for SQL
aggregations (`SettlementService`, `ReconciliationService`, `ReportService`) live in
Infrastructure - this is a pragmatic choice to avoid leaking EF Core into the Application
layer while still using LINQ / raw SQL where appropriate. The MVC portal and the report APIs
share the same `ReportService` instance so no query is duplicated.

## 2. Money storage

Every money column is `BIGINT` (long in C#), integer fils. 1 AED = 100 fils. No `DECIMAL`,
`float`, or `double` anywhere in money logic. Commission uses `decimal` *only* during the
`faceValueFils * rate / 100` multiplication, immediately rounded to `long` via
`Math.Round(value, MidpointRounding.AwayFromZero)` (half-up). The wallet balance column is a
denormalised cache of `SUM(ledger entries)` - updated inside the same transaction as the
ledger write and verified by `db/ledger_check.sql`.

## 3. Wallet concurrency strategy

`SELECT * FROM wallets WHERE partner_id = ? FOR UPDATE` at the start of every debit/refund,
inside the caller's DB transaction. This is pessimistic row locking - it serialises all
accesses to a given wallet, which is exactly what we want for money. The 200-parallel-debit
test confirms: exactly 150 succeed, 50 get `INSUFFICIENT_FUNDS`, balance never negative,
ledger balanced.

I rejected optimistic concurrency (version column + retry-on-conflict) because retries on a
hot wallet would amplify load under contention, and the assignment explicitly wants "exactly
150 succeed, 50 fail" - deterministic counts are easier with pessimistic locking.

Deadlock handling: the wallet lock is always acquired in the same order (wallet row, then
ledger insert, then topup insert) so there's no lock-ordering deadlock. If MySQL does throw a
lock-wait timeout (10s default, see `db/mysql.cnf`), the transaction rolls back and the API
returns a 500 - the partner can retry with the same idempotency key.

## 4. Idempotency strategy

Database-backed. The `idempotency_records` table has a UNIQUE constraint on
`(partner_id, idempotency_key)`. The flow:

1. Open DB transaction.
2. `INSERT INTO idempotency_records (status = InProgress, ...)` - on UNIQUE violation, the
   existing record is read; if still InProgress -> `409 REQUEST_IN_PROGRESS`; if Completed
   with same body hash -> replay; if Completed with different body hash -> `422
   IDEMPOTENCY_KEY_REUSED`.
3. Validate request body. If validation fails, **rollback** (the InProgress record is
   deleted) so the partner can fix the issue and retry with the same key.
4. Debit wallet (locks wallet row). If `INSUFFICIENT_FUNDS`, **rollback** - the partner can
   top up their wallet and retry with the same key.
5. Insert PENDING topup, mark idempotency Completed (status=202, response body, Location
   header), commit.

Keys expire after 24 hours (`expires_at_utc` column + a periodic cleanup - currently just a
delete in tests; production should run a scheduled job).

`(partner_id, client_ref)` UNIQUE on `topups` enforces DUPLICATE_CLIENT_REF (409) when a
new idempotency key is supplied for an existing clientRef.

## 5. HMAC secret storage

AES-GCM (96-bit nonce, 128-bit auth tag) with a 32-byte master key from
`Secrets:MasterKey` config (env var `TOPUPLEDGER_SECRET_MASTER_KEY` in production). The
plaintext secret never touches the database - only the ciphertext + nonce + tag are stored.
Decryption happens only in-memory in the `HmacAuthMiddleware` for the duration of one
signature verification. I chose AES-GCM (not BCrypt) because HMAC verification requires
recomputing the HMAC with the plaintext secret - the secret must be recoverable.

Master key management for production: rotate via a key-rotation table that maps
`key_version -> master_key`, store the version alongside each ciphertext. Not implemented
in this assignment but the `Partner` entity has room for a `SecretKeyVersion` column if
needed.

## 6. Refund and ledger strategy

Append-only ledger. A refund creates a new `wallet_ledger` row with `entry_type = CREDIT`
and `reason = TopupRefund`. The original `TopupDebit` row is never modified. The topup row
itself is locked `SELECT ... FOR UPDATE` during state transitions (`PENDING -> FAILED |
EXPIRED`) so that concurrent callbacks cannot double-refund. If a callback arrives after the
topup is terminal, the callback is logged in `callback_history` with outcome =
`IgnoredAfterTerminal` and no money moves.

## 7. Unresolved assignment ambiguity

**HMAC PDF signature vector (Section 5.2).** The OCR'd body produces the exact SHA256 from
the PDF (`bd1ab69d25906f9c0eccdbb3249655e08971a669e544bfdfe37fd59d55f75157`), confirming my
body parsing. However, the expected signature (`6a83bb072eb911c666f529bf1e45d907deff4882d27404e908e3a9d6f4d85c8d`)
does not reproduce from the OCR'd secret (`sk_test_EJ3NS3_UAE 2026_k7Rm` - note the space) or
the OCR'd idempotency key (`f3c2a91-5d4e-4b8a-9c1f-O0e6d2b7a4c55` - 33 chars, OCR likely
substituted `0` for `O` and added an extra `5`). I tried every reasonable interpretation
(with/without space, with/without leading slash, with/without trailing newline, with
various plausible UUID reconstructions) without match. My implementation is spec-correct;
the failing vector is `Skip`-documented in `HmacSignatureServiceTests.cs`. **Action needed:**
the exact plaintext secret + idempotency key from the assignment author would resolve this
in one test run.

## 8. Other decisions worth noting

- `business_date_utc` stores midnight UTC of the Dubai business day DATE (after 22:00 cutoff
  shift). Equality filters in SQL work directly because `WHERE business_date_utc = '2026-10-12'`
  parses to `2026-10-12 00:00:00 UTC`. The time-of-day portion is always 00:00 - it is NOT
  the UTC instant of midnight Dubai (which would be 20:00 UTC the previous day and break
  date equality filters).
- Default dev credentials are auto-seeded by `DevSecretSeeder` (Development only): admin
  passwords are re-hashed with proper BCrypt, partner secrets generated + shown once. This
  keeps `seed.sql` runnable without depending on the app.
- UTC +4 Dubai timezone is hardcoded via `TimeZoneInfo.CreateCustomTimeZone("Asia/Dubai", ...)`
  so the server OS timezone does not affect business-day calculation (the assignment warns
  about this explicitly).
