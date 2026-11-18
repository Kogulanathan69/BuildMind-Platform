# M4 delivery and QA report (BE-M4-D07, STEP 10-A)

- **Dates:** 2026-11-18/19.
- **Branch:** `feature/m4-infrastructure-final-qa` from `develop` @ `1ab1f98`.
- **Status:** QA corrections implemented, **NOT committed** (stopped for source review).
- **Not an independent QA approval:** this report is AI-prepared. The D07 acceptance criterion "independent QA approval" is **not met**.

## 1. Verified baseline evidence
- **PR #27:** merged on 2026-11-18T18:26:11Z as `1ab1f98`, with **0 reviews** (G19).
- **Post-merge CI:** run **#52** (id 44369690073, push, `1ab1f98`, success).
- **Downloaded artifacts:** all match run #52 by SHA-256 digest:

| Artifact | SHA-256 |
|---|---|
| `dotnet-test-results` | `19fcd1d6…` |
| `mysql-compatibility-results` | `5ca450cf…` |
| `python-test-results` | `f0865879…` |

- **Results, read independently:**

| Suite | Passed |
|---|---|
| Infrastructure | 412 |
| Requirements | 233 |
| Application | 128 |
| Workflow | 70 |
| Reports | 54 |
| MySQL + live Python | 56 |
| M3 contract checks | 246 |
| Python | 54 |
| **Total** | **1,253** (0 failed, 0 not executed) |

  - `PersistenceConcurrencyLiveTests`: 4/4.
  - `PythonLiveSmokeTests`: 7/7.
- **Migration artifacts:**
  - The three generated C# migration files are identical to the committed files.
  - `buildmind-ranged-from-0.sql` is byte-identical to run #50.
  - The two **synthetic spike** SQL files differ from run #50 **only** in the per-run generated migration ID (`yyyyMMddHHmmss_CiGeneratedSpike`). That is expected; they are not the product migration.
  - Correction to the STEP 10-A prompt: it said "four files byte-identical"; the precise state is above.

## 2. D01–D07 deliverable matrix
Classification legend:
- **RV**: implemented and runtime-verified in CI.
- **IU**: implemented but unverified.
- **PI**: partially implemented.
- **BL**: blocked by another member.
- **PA**: pending approval.
- **NI**: not implemented.

No deliverable is complete: every M4 PR was merged with 0 independent reviews, and the plan says "Done requires … review".

| Task | Item | Status | Evidence / reason |
|---|---|---|---|
| **D01** foundation and contracts | build, `/health`, dependency direction, solution, DI and module registration | **RV** | layer tests, run #52 |
| | error shape C3/C5, config keys C8 | **RV** | Problem Details tests |
| | CORS contract (C6), OpenAPI (C7) | **PA / NI** | decisions open |
| | current-user / password / token contract (A1/A2) | **PA** | `ICurrentUser` adapter exists; no token service |
| **D02** MySQL and identity | provider, DbContext, config validation, least-privilege CI accounts, migration on an empty DB | **RV** | MySQL 56/56 |
| | M1 User/Project mappings | **RV** (schema) / **BL** (repositories) | the M1 rehydration factories don't exist on any branch |
| | JWT validation middleware (item 04) | **NI / PA** | A1/A2 and M1 auth not approved; not simulated |
| | reset/rollback procedure doc | **RV** (CI ranged script) | |
| **D03** schema and migrations | schema, `InitialFeatureSchema`, FK/unique tests, requirement-revision repository | **RV** | |
| | land, survey, plan and generation entities (M2/M3/M6) | **BL** | no entity proposals or contracts |
| **D04** errors and shared security | exception mapping, safe 5xx, log redaction, readiness, security headers, middleware order | **RV** | |
| | CORS allowlist | **PA** (C6) | |
| | auth policies | **PA / BL** (A1/A2, M1) | |
| | request limits | **BL** (M2 upload limits) | Kestrel default 30 MB applies |
| | OpenAPI exposure review | **PA** (C7) | not exposed today |
| **D05** C#-to-Python | typed client, per-environment URL, timeouts, cancellation, `traceparent`, bulkhead, Python readiness, truthful 503/504, live smoke 7/7 | **RV** | |
| | bounded retry for idempotent calls | **NI by design**: no idempotent feature routes exist, so retrying is unsafe | |
| | internal authentication / network restriction (G11) | **PA** | |
| | M2 private storage adapter (item 04) | **BL** | no M2 storage contract |
| | "safe retry does not duplicate jobs" | **BL** | no job contract (M3/M6) |
| **D06** transactions and concurrency | `TransactionRunner`, lead-approved failure mapping, live concurrency tests | **RV** | run #52 |
| | idempotency for duplicate requests | **BL / PA** (C2/C5b) | |
| | production database permission policy (item 04) | **PA** (CI least-privilege only) | |
| | job/version keys with module owners (item 02) | **BL** | |
| **D07** regression and release readiness | clean CI setup and migration from empty | **RV** | |
| | vulnerability audit | **RV** (no vulnerable packages) | |
| | readiness abandoned-probe fix (STEP 10-A/10-B) | **IU** | compiled only |
| | C# + FastAPI auth and CORS verification (item 02) | **NI**: those components don't exist | |
| | setup doc (item 04) | **IU** (§8) | |
| | independent QA approval | **not met** | |

## 3. Implementation inventory (develop + this step)
- **API (composition root):**
  - `GlobalExceptionHandler` and `ProblemDetailsDefaults` (C3/C5);
  - `PersistenceProblemMapping` (D06);
  - `SecurityHeadersMiddleware`;
  - `HttpContextCurrentUser`;
  - options validation (`DatabaseOptionsValidation`, `ServicesOptionsValidation`, start-up validation);
  - readiness (`MySqlReadinessCheck`, `PythonReadinessCheck`, `InternalServiceReadinessCheck`, `ReadinessProbeGate`);
  - the internal transport factory;
  - route module registration (`/api/v1`).
- **Infrastructure:**
  - `BuildMindDbContext` with record configurations and UTC converters;
  - `TransactionRunner`;
  - `PersistenceErrorClassifier`;
  - `RequirementRevisionRepository`;
  - `InternalServiceClient`, `PythonServiceClient`, `InternalServiceBulkhead`;
  - `DatabaseOptions`, `ServicesOptions`.
- **CI:**
  - `dotnet-build` (with TRX and M3 checks);
  - `python-tests` (with JUnit);
  - `database-compatibility`:
    - pinned MySQL 8.4 by digest, per-run random passwords, TLS-only least-privilege accounts;
    - migration generate/verify, live MySQL and Python tests;
    - credential scan before upload, `always()` cleanup.

## 4. Database schema inventory (`20261118133137_InitialFeatureSchema`)
- **Tables:** `users`, `projects`, `requirement_revisions`, `project_requirement_heads`.
- **Foreign keys (4):** `fk_projects_users_owner_id`, `fk_requirement_revisions_projects_project_id`, `fk_project_requirement_heads_projects_project_id`, `fk_project_requirement_heads_requirement_revisions_current`.
- **Indexes (4):** `ux_users_normalized_email`, `ux_requirement_revisions_project_id_revision_number`, `ix_projects_owner_id_created_at_utc`, `ix_project_requirement_heads_project_id_current_revision_id`.
- **Columns:** 1 JSON column; timestamps `datetime(6)` UTC.
- **Concurrency:** `projects.revision` is a concurrency token.

## 5. QA audit findings (STEP 10-A)
### Defect fixed (M4-owned)
**D07-1:** an abandoned readiness probe could keep the single-flight gate.
- **Cause:** `WaitAsync(timeout)` and `CancelAfter(timeout)` use the same duration. When the wait won the race, the linked token source was disposed before it cancelled.
- **Effect:** the abandoned probe was never cancelled. It held the gate until the provider's own timeout, and readiness reported `check_in_progress` meanwhile (**503** for MySQL).
- **Fix:** `ProbeDeadline.Settle` (refined in STEP 10-B, see §13).
- **Tests:** +2 (5 consecutive timeouts each must report `timeout`, never `check_in_progress`, and release the gate).

### Areas audited with no defect
- Layer boundaries (layer tests).
- DI lifetimes: singletons are the gate, bulkhead and `TimeProvider`; the typed client is transient; DbContext and `TransactionRunner` are scoped. The Development test host validates scopes and the build.
- Options validation at start-up.
- Exception handling (D06 audit).
- No raw SQL anywhere: all EF LINQ, sensitive logging off.
- Every async call passes a token. `CancellationToken.None` is used only for commit, rollback and readiness continuations, all intentional.
- Disposal paths.
- Internal destination allowlist and bulkhead (D05 audits).
- Live correlation and no credentials on the wire (PY07).
- CI Python process cleanup (`always()`).
- Credential scan before the MySQL artifact upload. The dotnet and Python artifacts come from jobs that hold no secrets.

### Security and production findings (recorded, NOT implemented; not simulated)
| ID | Finding | Owner / decision |
|---|---|---|
| S1 | No authentication or authorization middleware: `ICurrentUser` is always anonymous | A1/A2, M1 |
| S2 | CORS not configured | C6 |
| S3 | `AllowedHosts: "*"`, no HTTPS redirection or HSTS | hosting decision (G11 / deployment) |
| S4 | No global request size policy (Kestrel default 30 MB), no rate limiting | M2 limits, lead |
| S5 | GitHub Actions pinned by major tag, not commit SHA | lead approval needed |
| S6 | FastAPI `/docs` and `/openapi.json` exposed | M6 |
| S7 | Internal service authentication / transport | G11 |
| S8 | Every M4 PR merged with 0 reviews | G19 |

## 6. Integration architecture
API → `PythonServiceClient` (allowlisted relative paths, own deadline, bulkhead of 64 per dependency, ≤32 connections, no redirects, proxy or cookies, 4 MB JSON limit, `traceparent`, no retries) → FastAPI `GET /health` only.

Failures map to 503/504 Problem Details. Readiness reports Python as Degraded only. G11 is open.

## 7. Test evidence (compiled vs executed)
| Suite | Executed (run #52) | Compiled now |
|---|---|---|
| Infrastructure | 412 | **417** (+5, not executed) |
| MySQL + live Python | 56 | 56 |
| Others (Requirements/Application/Workflow/Reports/M3) | 233 / 128 / 70 / 54 / 246 | unchanged |
| Python | 54 | 54 (passed locally) |
| **Total** | **1,253** | **1,258** |

## 8. Repeatable setup (names only, no values)
1. Install the .NET 10 SDK (CI pins it with a job-local `global.json`), MySQL 8.4 and Python 3.13.
2. Run `dotnet restore BuildMind.sln`, then `dotnet build -c Release`.
3. Create a database and a least-privilege runtime account (TLS required). Apply the reviewed ranged script (`dotnet ef migrations script 0`), which is Option B. Idempotent scripts are not supported by the provider.
4. Configuration (environment variables):
   - `ASPNETCORE_ENVIRONMENT`
   - `ConnectionStrings__BuildMind`
   - `Database__Enabled`
   - `Database__CommandTimeoutSeconds`
   - `Services__Python__Enabled`, `Services__Python__BaseUrl`, `Services__Python__TimeoutSeconds`
   - `Services__Rag__Enabled`, `Services__Rag__BaseUrl`, `Services__Rag__TimeoutSeconds`
5. Start Python on a private interface only: `python -m uvicorn app.main:app --host 127.0.0.1 --port <port>`.
6. Check `/health` (liveness) and `/health/ready` (MySQL mandatory; Python and RAG Degraded when absent).
7. Test variables: `BUILDMIND_TEST_MYSQL`, `BUILDMIND_TEST_MYSQL_RUNTIME`, `BUILDMIND_TEST_PYTHON_URL`.

**Operational limitations:**
- Windows Smart App Control blocks local C# test execution (G17), so Linux CI is the verification path.
- No authentication, so no feature endpoint can be used for real.

## 9. Cross-member dependencies (state on 2026-11-19)
- **`develop`:** only M4 merges since `fdf7baa`.
- **M1:** `feature/m1-auth-projects` is unmerged, and no `User`/`Project` rehydration factory exists on any branch.
- **M3:** PR #25 is still a draft.
- **M5:** **PR #28** (open, not a draft): 12 files, dashboard only. It touches no M4-owned file and is behind `develop`; it needs review under M7.
- **M2 and M6:** no persistence, storage or job contracts.

**Result:** no previously blocked M4 deliverable became unblocked.

## 10. Required approvals
- A1/A2 (auth).
- C6 (CORS).
- C7 (OpenAPI).
- C2/C5b (idempotency).
- G11 (internal transport, auth and hosting).
- Hosting: AllowedHosts, HTTPS/HSTS.
- Request size and rate limits.
- Action SHA pinning.
- Production database account policy.
- G03, G12, G17, G19 (independent reviews of PRs #8–#27), G20.

## 11. Production readiness assessment
**NOT production-ready.**
- **Infrastructure quality (error handling, persistence, integration, CI):** strong and runtime-verified.
- **Missing security essentials:** authentication and authorization, CORS, the hosting and TLS posture, request limits and rate limiting, internal service authentication.
- **No independent review** of any M4 PR.

## 12. Remaining M4 development tasks
1. JWT validation and auth policies, once A1/A2 are approved and M1 delivers.
2. CORS, once C6 is approved.
3. Remaining repositories, once the M1 factories and M2/M3/M6 contracts exist.
4. The M2 storage adapter.
5. Internal authentication, once G11 is decided.
6. Idempotency and job deduplication, once C2/C5b are decided.
7. Request limits and rate limiting.
8. The production database account policy.
9. Independent QA approval of this report and of PRs #8–#27.

## 13. STEP 10-B readiness correction audit (2026-11-19)

| Check | Result |
|---|---|
| Both MySQL and Python abandoned probes are cancelled | ✅ `ProbeDeadline.Settle` in both `finally` blocks |
| Cancellation happens before the source is disposed | ✅ the source is no longer `using`-scoped; `Settle` is its only disposer |
| Gate held until the real probe finishes | ✅ released by the probe's own continuation |
| Gate released after completion or cleanup failure | ✅ `ReleaseAsync` try/finally (MySQL); a plain release (Python, no scope) |
| Caller cancellation distinguishable from timeout | ✅ rethrown when the caller's token is cancelled; otherwise `timeout` |
| Callback failures cannot leak | ✅ observed and dropped; never logged or returned |
| **Blocking callbacks cannot defeat the deadline** | **Defect R2 (fixed).** The 10-A `Cancel()` ran callbacks synchronously on the readiness thread, so a blocking callback (a provider may open a second connection to kill a query) delayed the result past the deadline. Now `CancelAsync()` runs callbacks off-thread. The source is disposed only after BOTH the probe and the callbacks finish (10-A disposed it at method exit, possibly while a callback still ran) |
| Non-cooperative probes cannot accumulate | ✅ at most one outstanding probe per check (single-flight). A probe that never ends keeps reporting `check_in_progress`, which is truthful |
| Tests reliable and deterministic | The two 10-A tests pass deterministically, but whether they **detect** the original race is probabilistic (timer versus wait). Added 3 **deterministic** `Settle` tests: a blocking callback doesn't delay `Settle` (< 2 s) and the source stays alive until the probe ends; callback and probe failures are dropped and the source is still disposed; a finished probe is disposed immediately and never cancelled |

Infrastructure **417** compiled (412 executed + 5 new). Full backend **1,258**.
