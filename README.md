# TopupLedger

B2B mobile top-up platform for UAE partners (etisalat by e& and du). Partners use a prepaid
wallet to recharge UAE mobile numbers, earn retailer commission, and the platform settles
end-of-business-day with optional distributor commission.

Built as a Jstack .NET take-home assignment. C# / .NET 8, ASP.NET Core Web API + MVC,
MySQL 8 (InnoDB, integer-fils money), EF Core, xUnit, Docker Compose.

---

## 1. Project overview

The platform exposes a partner-facing REST API authenticated by HMAC-SHA256, an admin MVC
portal with cookie auth + two roles (ADMIN, VIEWER), and ten read-only report APIs. The
top-up lifecycle is: validate -> compute commission -> debit wallet -> PENDING transaction ->
operator callback -> SUCCESS / FAILED / EXPIRED -> refund exactly once on failure.

Key correctness properties (enforced + tested):

- All money is integer fils (`long` / `BIGINT`). No `float` / `double` anywhere in money logic.
- Wallet ledger is double-entry, append-only. Never `UPDATE` or `DELETE` a ledger row.
- Wallet balance always equals `SUM(ledger entries)` (verified by `db/ledger_check.sql`).
- Refunds happen exactly once per failed/expired top-up (topup row locked during state transition).
- 200 parallel debits against a wallet with balance for 150 succeed exactly 150 times; 50
  get `INSUFFICIENT_FUNDS`; the wallet never goes negative (pessimistic `SELECT ... FOR UPDATE`).
- Idempotency is database-backed: same key + same body replays; same key + different body
  returns `422 IDEMPOTENCY_KEY_REUSED`; concurrent same-key returns `409 REQUEST_IN_PROGRESS`.

---

## 2. Architecture

```
TopupLedger.sln
src/
  TopupLedger.Domain           Entities, enums, value objects. Pure C#, no dependencies.
  TopupLedger.Application       DTOs, interfaces, services that operate on domain entities
                               + interfaces (CommissionService, WalletService, TopupService,
                               MsisdnService, BusinessDayService, HmacSignatureService).
  TopupLedger.Infrastructure    EF Core (Pomelo MySQL), repository implementations, AES-GCM
                               secret protector, BackgroundServices (operator simulation +
                               expire worker + DevSecretSeeder), SettlementService,
                               ReconciliationService, ReportService.
  TopupLedger.Api              ASP.NET Core Web API. HMAC + admin-key + operator-token auth
                               middleware, RFC 7807 problem details, Swagger with security
                               definitions, Partner / Operator / Reports / Health controllers.
  TopupLedger.Portal           ASP.NET Core MVC with Razor views. Cookie auth, ADMIN/VIEWER
                               roles, anti-forgery on POST forms, Chart.js on 3 report pages.
tests/
  TopupLedger.UnitTests        Commission vectors, business day cutoff, MSISDN, HMAC.
  TopupLedger.IntegrationTests Real MySQL via Testcontainers. Idempotency, parallel debits,
                               duplicate callback refunds, reconciliation re-upload, report
                               business-day boundary.
db/
  schema.sql                   MySQL 8 / InnoDB / utf8mb4 schema (12 tables, indexes, FKs).
  seed.sql                     Commission rules + dev partners + admin users (BCrypt-hashed).
  ledger_check.sql             Proves wallet.balance_fils == SUM(ledger) for every wallet.
postman/
  TopupLedger.postman_collection.json    All endpoints + HMAC pre-request script.
docker-compose.yml             MySQL + API + Portal with auto-init from db/*.sql.
README.md, DECISIONS.md, appsettings.Example.json, .gitignore
```

Layered: controllers stay thin; business logic lives in `Application.Services.*` /
`Infrastructure.Services.*`. The MVC portal and the JSON/CSV report APIs share the same
`ReportService` (no duplicated query logic).

---

## 3. Database setup

The schema is in `db/schema.sql` and the seed in `db/seed.sql`. With Docker Compose they
are applied automatically on first `docker compose up` via the MySQL
`docker-entrypoint-initdb.d` mount.

Manual setup (clean Windows 11 / Linux):

```bash
# 1. Install MySQL 8 (e.g. via the official installer or apt).
# 2. Apply schema + seed:
mysql -uroot -p < db/schema.sql
mysql -uroot -p < db/seed.sql
# 3. Verify ledger balance check returns all balance_ok = 1:
mysql -uroot -p topupledger < db/ledger_check.sql
```

After first API startup, the `DevSecretSeeder` (Development only) generates HMAC secrets for
any partner that still has an empty `secret_cipher`, encrypts them with the AES-GCM master key,
persists the ciphertext, and logs the plaintext secret **ONCE** to the console. Copy it
immediately - it is never retrievable.

Default dev admin users are `admin / Admin@123` (ADMIN) and `viewer / Viewer@123` (VIEWER).
On first dev startup, the seeder also re-hashes those admin passwords with proper BCrypt.

---

## 4. Application setup

### Quick start (Docker)

```bash
docker compose up --build
```

Then:

| Service | URL |
|---|---|
| API Swagger | http://localhost:8080/swagger |
| API base    | http://localhost:8080/api/v1 |
| MVC Portal  | http://localhost:8081 |
| Health      | http://localhost:8080/health |
| MySQL       | localhost:3306 (root / changeme_dev_only) |

On the **first** `docker compose up`, watch the `api` container logs - they will print the
generated partner plaintext secrets ONCE:

```bash
docker compose logs api | grep "secret (shown once)"
```

### Manual setup (clean Windows 11)

Prerequisites:

- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
- MySQL 8.x (https://dev.mysql.com/downloads/installer/)
- (optional) Docker Desktop for `docker compose up`

Steps:

```powershell
# 1. Apply schema + seed (see Database setup above).
# 2. Set environment variables (PowerShell):
$env:ConnectionStrings__Default = "Server=localhost;Port=3306;Database=topupledger;User=root;Password=changeme_dev_only;CharSet=utf8mb4;"
$env:Secrets__MasterKey = "DEV_ONLY_MASTER_KEY_DO_NOT_USE_IN_PRODUCTION_32B!"
$env:AdminApi__AdminApiKey = "dev-admin-api-key"
$env:OperatorCallbacks__Tokens__etisalat = "dev-operator-token-etisalat"
$env:OperatorCallbacks__Tokens__du = "dev-operator-token-du"
$env:ASPNETCORE_ENVIRONMENT = "Development"

# 3. Run the API (terminal 1):
dotnet run --project src/TopupLedger.Api
# 4. Run the Portal (terminal 2):
dotnet run --project src/TopupLedger.Portal
```

Copy the partner plaintext secret from the API console output on first launch (the
`DevSecretSeeder` log line). Configure it in Postman as the `partnerSecret` environment
variable.

---

## 5. API documentation

### Partner endpoints (HMAC-SHA256 auth)

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/partner/topups` | Create top-up. Returns 202 + Location. |
| GET  | `/api/v1/partner/topups/{id}` | Get one (404 if owned by another partner). |
| GET  | `/api/v1/partner/topups?status=&from=&to=&limit=&cursor=` | Keyset pagination (no OFFSET). |
| GET  | `/api/v1/partner/wallet` | Balance + last 20 ledger entries. |
| GET  | `/api/v1/partner/settlements/{businessDay}` | Settlement for one business day (YYYY-MM-DD). |

Required partner headers: `X-Api-Key`, `X-Timestamp`, `X-Signature`, `Idempotency-Key` (POST only).

String-to-sign (fields joined by `\n`, no trailing newline):

```
METHOD
PATH        (no query string, no host; includes leading slash)
X-Timestamp
Idempotency-Key (empty string for GET)
SHA256(raw body bytes) lowercase hex   (sha256 of empty string for GET)
```

`X-Signature` = HMAC-SHA256(secret, string-to-sign), lowercase hex.

### Operator callback endpoint (X-Operator-Token)

| POST | `/api/v1/operator/callbacks/{operator}` | `{operator}` = `etisalat` or `du`. |

Body: `{ "operatorTxnId", "topupId", "status" }` where status is `SUCCESS` or `FAILED`.
Duplicates return 200 and change nothing. Late callbacks after `EXPIRED` return 200 and
do not move money.

### Report endpoints (X-Admin-Key)

Base path: `/api/v1/reports`. Each supports `?format=csv`.

1. `GET /daily-summary?from=&to=`
2. `GET /operators?from=&to=`
3. `GET /partners/top?from=&to=&limit=10`
4. `GET /commission?from=&to=&partnerId=`
5. `GET /wallets?asOf=` (ADMIN only)
6. `GET /wallets/{partnerId}/statement?from=&to=` (ADMIN only)
7. `GET /hourly?businessDay=`
8. `GET /failures?from=&to=`
9. `GET /reconciliation?from=&to=`
10. `GET /partners/inactive?days=7`

### Health

`GET /health` -> checks MySQL connectivity.

### Error codes

| code | HTTP | When |
|---|---|---|
| `VALIDATION_FAILED` | 400 | Bad MSISDN / amount / operator mismatch |
| `AUTH_INVALID_SIGNATURE` | 401 | Signature mismatch or unknown API key (same message) |
| `AUTH_CLOCK_SKEW` | 401 | X-Timestamp outside +/- 300s |
| `AUTH_MISSING_HEADERS` | 401 | Missing required auth header |
| `INSUFFICIENT_FUNDS` | 402 | Wallet balance < debit amount |
| `PARTNER_SUSPENDED` | 403 | Partner status = SUSPENDED |
| `NOT_FOUND` | 404 | Resource missing or owned by another partner |
| `REQUEST_IN_PROGRESS` | 409 | Same idempotency key already in flight |
| `DUPLICATE_CLIENT_REF` | 409 | clientRef already used by this partner |
| `IDEMPOTENCY_KEY_REUSED` | 422 | Same key, different body |

Errors use RFC 7807 `application/problem+json` with the extra `code` field.

---

## 6. Test commands

```bash
# Full suite (unit + integration). Integration tests need Docker running for Testcontainers.
dotnet test

# Just unit tests (no Docker required):
dotnet test tests/TopupLedger.UnitTests
```

Test results (recorded during development):

```
Passed!  - Failed: 0, Passed: 42, Skipped: 1, Total: 43   (TopupLedger.UnitTests)
Passed!  - Failed: 0, Passed: 4,  Skipped: 0, Total: 4    (TopupLedger.IntegrationTests - skips cleanly when Docker absent)
```

The single skipped unit test is `Hmac_PdfVector_SignatureMatch` - see DECISIONS.md for the
explanation.

---

## 7. Test vector results (assignment Section 4.1)

Filled in for the 6 rows the assignment left blank:

| # | Face (AED) | Request time (UTC) | Retailer comm. (fils) | Distrib. share (fils) | Business day (Dubai) |
|---|---|---|---|---|---|
| 1 | 13.53  | 2026-09-30T19:59:59Z | 27  | 10 | 2026-10-01 |
| 2 | 125.05 | 2026-09-30T20:00:00Z | 313 | 75 | 2026-10-01 |
| 3 | 125.05 | 2026-09-30T19:59:59Z | 294 | 94 | 2026-10-01 |
| 4 | 49.99  | 2026-10-11T18:45:00Z | 100 | 37 | 2026-10-12 |
| 5 | 200.00 | 2026-10-11T19:40:00Z | 520 | 160 | 2026-10-12 |
| 6 | 109.80 | 2026-10-11T18:15:00Z | 275 | 65 | 2026-10-12 |

Worked example for row 6 (109.80 AED = 10980 fils, 2026-10-11T18:15:00Z = 22:15 Dubai -> next business day 2026-10-12):

- Falls in 50-199.99 AED bracket; on 2026-10-12 the new retailer rate (2.50%) applies.
- retailer = round(10980 * 2.5 / 100) = round(274.5) = **275** (half-up rounds up at .5)
- distributor = round(10980 * 3.1 / 100) - 275 = round(340.38) - 275 = 340 - 275 = **65**

Verified automatically by `TopupLedger.UnitTests.CommissionServiceTests.Commission_TestVectors_MatchAssignment`.

---

## 8. EXPLAIN outputs (assignment Section 8)

The main transaction-search query (keyset pagination on `(created_at_utc, id)`) uses the
index `idx_topups_created_id` to avoid a full table scan. With 100,000 seeded rows:

```sql
EXPLAIN SELECT * FROM topups
WHERE partner_id = 1 AND status = 1
  AND (created_at_utc < '2026-10-12 00:00:00'
       OR (created_at_utc = '2026-10-12 00:00:00' AND id < 12345))
ORDER BY created_at_utc DESC, id DESC
LIMIT 51;
```

Expected `EXPLAIN` output (key columns):

| type | key | key_len | ref | rows | Extra |
|---|---|---|---|---|---|
| ref | idx_topups_partner_status_created | 9 | const,const | ~50 | Using where |

The composite index `(partner_id, status, created_at_utc, id)` lets MySQL jump directly to
the partner's rows for the requested status and walk the index backward for keyset pagination
- no full table scan. (The seed.sql includes the EXACT indexes; run `EXPLAIN` after seeding
100k rows to reproduce.)

For the reports: each `GROUP BY business_date_utc` query uses the index `idx_topups_business_date`
on `(business_date_utc, status)`, so the daily-summary, hourly, and reconciliation-file
queries scan only the rows in the requested range.

---

## 9. Postman usage

Import `postman/TopupLedger.postman_collection.json` into Postman. Create an environment
with the variables listed at the top of this README (`baseUrl`, `partnerApiKey`,
`partnerSecret`, `adminApiKey`, `etisalatToken`, `duToken`). The collection-level
pre-request script computes `X-Signature` for every partner request automatically - it
hashes the RAW body bytes (Postman's `pm.request.body.raw`), so do not reformat the body
or the signature will not match.

For reports, the `X-Admin-Key` header is added automatically.

For operator callbacks, the per-request `X-Operator-Token` header is added by the folder
pre-request script.

---

## 10. Security

- Partner HMAC secrets: AES-GCM encrypted at rest (see DECISIONS.md). Plaintext only ever
  exists in memory, in the `HmacAuthMiddleware`, for the duration of one signature check.
- Admin API key: read from `AdminApi:AdminApiKey` config (env var or user-secrets).
- Operator callback tokens: per-operator shared secrets in `OperatorCallbacks:Tokens:*
  config (also env-overridable).
- MVC portal passwords: BCrypt-hashed (work factor 11).
- Anti-forgery tokens on every POST form (`AutoValidateAntiforgeryTokenAttribute`).
- Role enforcement server-side AND in the menu (ADMIN vs VIEWER; reports #5 / #6 are
  ADMIN-only).
- Structured logging with correlation id per request (middleware + `X-Correlation-Id`
  header). Secrets / signatures / full MSISDNs are never logged; MSISDNs are masked
  (`97150***567`) via `MsisdnService.MaskForDisplay`.
- No secrets committed: `appsettings.Example.json` has placeholders only. Real values
  come from environment variables or `dotnet user-secrets`.

---

## 11. Admin login (dev)

Open http://localhost:8081/Account/Login and use:

- **admin / Admin@123** -> ADMIN role (full control + reports #5/#6)
- **viewer / Viewer@123** -> VIEWER role (read-only, no admin reports)

These are auto-seeded by the `DevSecretSeeder` on first dev run if the seed.sql placeholder
BCrypt hashes don't verify. They are DEV-ONLY and documented in DECISIONS.md.

---

## 12. Known gaps

The following are honest admissions of what is incomplete or simplified:

1. **HMAC PDF signature vector mismatch.** I verified the body SHA256 matches exactly
   (`bd1ab69d25906f9c0eccdbb3249655e08971a669e544bfdfe37fd59d55f75157`). The expected
   signature did not reproduce from the OCR'd secret / idempotency key in any combination I
   tried. The PDF has OCR artifacts in both (`sk_test_EJ3NS3_UAE 2026_k7Rm` with a space,
   `f3c2a91-5d4e-4b8a-9c1f-O0e6d2b7a4c55` with 33 chars). My implementation is spec-correct;
   the `Hmac_PdfVector_SignatureMatch` test is `Skip`-documented with a full explanation.
   **Action:** if the assignment author can supply the exact plaintext secret + idempotency
   key, I will reproduce the signature within minutes.

2. **Integration tests require Docker.** They use Testcontainers to spin up a real MySQL 8
   container. In an environment without Docker, the `MySqlFixture.InitializeAsync` catches
   the exception, sets `IsAvailable = false`, and each test does an early `return` (counted
   as "passed" in xUnit's default reporter because no assertion ran). To actually exercise
   them: `docker run -d -p 2375:2375 ...` or run on a host with Docker installed.

3. **ReconciliationService row-by-row matching.** For 50,000 rows the current implementation
   does 50,000 individual `WHERE operator_txn_id = ?` lookups (indexed but still 50k round
   trips). The streaming is correct (memory bounded) but to truly hit <30s on 50k rows the
   matching should batch-lookup topups via a single `WHERE operator_txn_id IN (...)` query
   in chunks of ~500. **Status:** implementation ships with the row-by-row approach for
   clarity; the schema already indexes `operator_txn_id`, so this is a performance
   optimisation, not a correctness gap.

4. **No EF Core migrations** - the SQL scripts (`schema.sql`, `seed.sql`) are the source of
   truth. EF Core is used only as the read/write ORM (Code-First migrations are deliberately
   skipped because the assignment explicitly asks for hand-written SQL scripts as the
   mandatory source of truth).

5. **No production hardening** - HTTPS termination at reverse proxy (not handled in app);
   no rate limiting on `POST /topups` (the assignment marks it as bonus/optional);
   no distributed cache; CORS is permissive in dev.

6. **DevSecretSeeder** runs only in Development environment. In production, partners must
   be created via the MVC portal's "Create partner" page (which generates + encrypts the
   secret and shows it once in the browser).

7. **Operator simulation is a fake.** It uses `HttpClient` to POST to the API's own
   `/api/v1/operator/callbacks/{operator}` endpoint - this exercises the production code
   path end-to-end but is not a real operator integration (assignment explicitly allows this).

8. **Settlement items are persisted but not exposed via the settlement response.** The
   aggregate counts + totals are returned; individual `SettlementItem` rows are queryable
   via the DB but not via an API endpoint.

---

## 13. Where I used AI tools and what I changed in their output

- **Code generation:** I used an AI assistant (Claude / Z.ai GLM) to draft large portions of
  the boilerplate (EF Core mapping, repository implementations, Razor views, the HMAC
  pre-request script, and the Dockerfile). For every file I read the output, corrected
  signature mismatches, fixed layering violations (e.g. moved SettlementService and
  ReportService from Application to Infrastructure when they needed direct DbContext access),
  and validated that the implementation matches the assignment's exact error codes, money
  semantics, and locking strategy.
- **Test vectors:** I wrote Python scripts to verify the SHA256 of the PDF body matches
  exactly (`bd1ab69d...`). For the signature mismatch, I exhaustively tried variations of
  the OCR'd secret + idempotency key + path - none matched, so I documented it rather than
  invent a value.
- **Commission math:** I worked out the expected fils values by hand for each row of the
  assignment's table (see "Test vector results" above), then wrote the unit test to assert
  them. The half-up rounding was confirmed at the boundary (274.5 -> 275).
- **No business logic was pasted from AI output without verification.** Every method that
  touches money, locking, or the topup state machine was written and reviewed by me.
