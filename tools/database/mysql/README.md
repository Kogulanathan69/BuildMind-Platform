# MySQL development and test accounts (BE-M4-D02a proposal)

[`001-dev-test-accounts.sql`](001-dev-test-accounts.sql) creates `buildmind_dev` and three least-privilege accounts limited to `localhost`. **Nothing runs it automatically.** The lead reviews it and runs it manually on a local development server.

| Account | Purpose | Privileges |
|---|---|---|
| `bm_runtime` | Application at run time | `SELECT, INSERT, UPDATE, DELETE` on `buildmind_dev.*` |
| `bm_migrator` | Applying reviewed migration scripts | DDL and data privileges on `buildmind_dev.*`; no `GRANT OPTION` |
| `bm_test` | Disposable spike and test databases | `ALL` on `buildmind\_test\_%` only |

## Steps (lead)

1. Open a DBA session to the **local** server. Check the port first; it is not assumed to be 3306. Run the pre-check line first: you need `partial_revokes = 0` and `have_ssl = YES`, otherwise stop. All three accounts `REQUIRE SSL`.
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

**Migrations are never applied by the application.** The process: generate an idempotent script (`dotnet tool run dotnet-ef migrations script --idempotent --project src/BuildMind.Infrastructure --startup-project src/BuildMind.API`), review it, then apply it with `bm_migrator`.

**Production** is out of scope here. It needs its own identities, TLS with certificate validation (`SslMode=VerifyFull`) and a secret store, and the G03 licence review must be accepted first.
