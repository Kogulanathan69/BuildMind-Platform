# MySQL development and test accounts (BE-M4-D02a proposal)

[`001-dev-test-accounts.sql`](001-dev-test-accounts.sql) creates `buildmind_dev` and three least-privilege accounts limited to `localhost`. **Nothing runs it automatically.** The lead reviews it and runs it manually on a local development server.

| Account | Purpose | Privileges |
|---|---|---|
| `bm_runtime` | Application at run time | `SELECT, INSERT, UPDATE, DELETE` on `buildmind_dev.*` |
| `bm_migrator` | Applying reviewed migration scripts | DDL and data privileges on `buildmind_dev.*`; no `GRANT OPTION` |
| `bm_test` | Disposable spike and test databases | `ALL` on `buildmind\_test\_%` only |

## Steps (lead)

1. Open a DBA session to the **local** server. Check the port first; it is not assumed to be 3306. Run the pre-check line first: you need `partial_revokes = 0` and a `tls_version` that lists TLSv1.2 or TLSv1.3, otherwise stop. All three accounts `REQUIRE SSL`.
2. Copy the script into the session. Replace the three placeholders with newly generated passwords, inside the session only.
3. Run it, and check that the `SHOW GRANTS` output matches the table above.
4. Store the secrets locally, never in the repository:
   ```sh
   # Runtime (API): User Secrets
   dotnet user-secrets set "ConnectionStrings:BuildMind" "Server=localhost;Port=<port>;Database=buildmind_dev;User ID=bm_runtime;SslMode=Required;Password=<...>" --project src/BuildMind.API
   # Migrations (dotnet-ef): environment variable for the session only
   #   BUILDMIND_MIGRATIONS_CONNECTION=Server=localhost;Port=<port>;Database=buildmind_dev;User ID=bm_migrator;SslMode=Required;Password=<...>
   # Spike tests: environment variables for the session only
   #   BUILDMIND_TEST_MYSQL=Server=localhost;Port=<port>;User ID=bm_test;SslMode=Required;Password=<...>
   #   BUILDMIND_TEST_MYSQL_RUNTIME=Server=localhost;Port=<port>;Database=buildmind_dev;User ID=bm_runtime;SslMode=Required;Password=<...>
   ```
5. Turn on database access in the API only when it's needed: `Database:Enabled=true`, set through User Secrets or an environment variable.

**Migrations are never applied by the application.** Process:
1. Find the last applied migration (`SELECT MigrationId FROM __EFMigrationsHistory`).
2. Generate a **ranged** script from it: `dotnet tool run dotnet-ef migrations script <last-applied> <target> --project src/BuildMind.Infrastructure --startup-project src/BuildMind.API`.
3. Review the script, then apply it once with `bm_migrator`. The history rows it inserts prevent reapplication.

**Don't use `--idempotent`.** With `MySql.EntityFrameworkCore` 10.0.12, idempotent scripts contain SQL Server-style `IF NOT EXISTS(...) BEGIN … END;` blocks that MySQL rejects (CI T06, 2026-11-18). This is an open provider limitation. MySQL DDL also auto-commits, so a failed script can leave a partly applied migration: review it and apply it during a maintenance window.

**Production** is out of scope here. It needs its own identities, TLS with certificate validation (`SslMode=VerifyFull`) and a secret store, and the G03 licence review must be accepted first.
