# Security and nonfunctional controls

Threat model: account takeover, IDOR, uploaded malicious documents, unsafe path traversal, secret leakage, SQL injection, stale edits, arbitrary internal URLs, prompt injection, unauthorized report sharing, denial of service. Enforce server-side ownership checks on every resource, not only UI guards. JWT signature/issuer/audience/expiry; short-lived single-use hashed recovery tokens; rate-limit auth; generic forgot-password response; role/module policies; never log secrets or password hashes. CORS exact configured origins. HTTPS for public environments; private Python service access authenticated, fixed allowlisted base URLs. Do not forward arbitrary user URLs or private credentials.

Upload: allowlist formats PDF/JPG/PNG only per approved contract, content signatures, configurable limits, generated private storage IDs, scan/quarantine policy, safe temp directory, isolated parser/OCR with time/resource bounds, no raw public paths, authorized downloads. Report data snapshot and recipient consent; expiring access if approved. No real customer files in public repo, fixtures synthetic. Database least privilege, secrets via local secret store/environment/deployment vault, backups and restore test. Security audit history is separate from UI visibility. Limit model input/output and treat retrieved text as untrusted; no silent edits. Never expose internal Vastu heuristic. Service errors map to safe Problem Details. Review licenses, storage retention, data residency and production deployment security before release.

## Implemented controls — BE-M4-D01c (2026-11-18; merged `0a4d70a`, CI passed, independent review outstanding)
- Public errors: fixed safe `detail` texts; no exception message, stack trace, SQL, connection string, path or internal URL in any environment; unsafe extension keys stripped; covered by Development and Production tests with synthetic secret markers.
- Identity: `ICurrentUser` uses only authenticated identities' claims; headers, query strings, bodies and unvalidated bearer values are ignored (spoofing test); missing/malformed/empty/conflicting identifiers fail closed; roles never assumed. **No authentication handler exists yet** (JWT, A1, A2 pending).
- Configuration: internal service URLs validated (http/https, no embedded credentials/query/fragment); validation messages never echo values; no secrets in `appsettings.json`; User Secrets ID configured, store not populated.
- Logging: trace-correlated structured logs; Production logs exception type only; escaped paths (no log-line injection); framework exception-middleware diagnostics suppressed only for exceptions BuildMind's handler handled (handler failures remain visible); no request/response body logging.
- HTTP 500 problems are always reduced to generic fields, even when an endpoint supplies its own detail/extensions; other statuses rely on endpoints keeping `detail`/extensions free of sensitive data.
- Current user: identifier and roles come from exactly one authenticated identity; claims of unrelated identities are never combined.
- Not yet implemented: CORS, OpenAPI exposure control, rate limiting, readiness, security audit store.

### Test reliability note — start-up validation (2026-11-18, `2d2a474`)

Start-up failure tests must prove the failure is an `OptionsValidationException`. With minimal hosting, `WebApplicationFactory` can surface `ObjectDisposedException` instead (start-up race). That exception is accepted only when the host itself recorded an `OptionsValidationException`; the credential-leak logging test also requires the host failure entry to be captured, so it cannot pass with empty logs.

### Database controls — BE-M4-D02a (2026-11-18, uncommitted)

- Connection strings are never committed: they come only from `ConnectionStrings:BuildMind` or `BUILDMIND_MIGRATIONS_CONNECTION` (User Secrets or environment variables). Validation messages name keys only; `root`, `Persist Security Info` and `Allow Load Local Infile` are rejected.
- No automatic database creation or migration. Least-privilege accounts are proposed: `bm_runtime` (DML only), `bm_migrator` (DDL on `buildmind_dev`), `bm_test` (`buildmind\_test\_%` only).
- EF sensitive-data logging and detailed errors are off. EF failure events that quote provider messages (which can contain stored values such as emails) are suppressed. The error classifier keeps only kind, error number and constraint name.
- A commit failure is reported as an unknown outcome and never retried.

- (STEP 05-C) Database TLS is mandatory: `SslMode` must be Required, VerifyCA or VerifyFull (production: VerifyFull), and the proposed accounts `REQUIRE SSL`. After a rollback or an unknown commit, tracked entities are discarded.

- (D02b) Server identity: `SslMode=VerifyFull` is required outside Development and for any non-loopback server. A rollback that can't be confirmed is reported as such, never as a clean rollback. CI workflow token is read-only and runs on `pull_request` only; database CI credentials are random per run, masked and never uploaded.

- (D02b) Timestamps are stored as unambiguous UTC (`datetime(6)`); non-UTC `DateTime` values are rejected. Migrations: single authorized executor, history check, backup before DDL, no automatic or production migrations.

- (D03) Repositories filter every query by project; ports are not authorization guards, so callers must establish ownership first. Constraint violations are translated without values (for example a duplicate email); stored customer content is never echoed in errors. FKs use RESTRICT (no cascading deletes).

### Implemented controls — BE-M4-D04 (2026-11-18, uncommitted)

- **Readiness** `GET /health/ready` (C11b): MySQL mandatory (503 when not configured or unreachable); Python/RAG Degraded and never Healthy until real checks exist (D05). Response = names, statuses and fixed reason codes; no hosts, users, exception text or connection details. `/health` liveness unchanged and dependency-free.
- **Response headers** on every response (including errors): `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`; `/api` responses default to `Cache-Control: no-store`. Kestrel `Server` header disabled.
- **Log hygiene regression:** bearer tokens, cookies, API keys and query-string tokens never appear in logs (tested in Production and Development). Precondition: `Microsoft.AspNetCore` stays at `Warning` (request-start logs include the query string), and **tokens must never be sent in query strings**.
- **Not yet in place (blocked):** CORS allowlist (C6), authentication/JWT and shared authorization policies (A1/A2, M1), request body limits (M2; Kestrel's 30 MB default applies), OpenAPI (C7, Development only). Until authentication exists, `ICurrentUser` is always anonymous and no endpoint may rely on it for ownership.

- (STEP 07-B) EF Core `ConnectionError` events are suppressed (they logged the server host and database name). MySQL readiness enforces its own 3 s deadline and runs at most one probe at a time per host; abandoned probes dispose their scope when they finish. The Kestrel `Server` header setting does not control headers added by a reverse proxy or load balancer.
