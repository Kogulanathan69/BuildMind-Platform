# BuildMind.Persistence.IntegrationTests — MySQL provider compatibility spike (BE-M4-D02)

**Status:** the tests are written and compile. They have **not been run**. G03 has conditional approval for internal development and compatibility testing only; the commercial licence review is still pending.

This project is **not in `BuildMind.sln`** on purpose, so the existing CI job never runs it. It runs only explicitly, against a **disposable** MySQL 8.4 server: either the proposed database CI job (needs approval) or an approved local environment. On the lead's workstation, Smart App Control currently blocks local C# test execution (G17).

## Safety rules

- Every test creates its own `buildmind_test_<32 hex>` database and drops it afterwards. The helper refuses to touch any other database name.
- Accounts: `bm_test` for the disposable databases and `bm_runtime` for the read-only privilege probe. Never use `root`; the helper rejects it.
- Credentials come only from environment variables. They are never committed, printed or logged.
- The model is synthetic (`spike_owners`, `spike_items`). It is **not** a BuildMind feature schema.

## Environment variables

| Variable | Account | Content |
|---|---|---|
| `BUILDMIND_TEST_MYSQL` | `bm_test` | Server, port, user, password and `SslMode=Required` (TLS is mandatory). Any database name is ignored. |
| `BUILDMIND_TEST_MYSQL_RUNTIME` | `bm_runtime` | The schema the runtime grants cover (`Database=buildmind_dev`) plus `SslMode=Required`. The probe connects **without** selecting that database. It only reads `SHOW GRANTS` (expecting DML on exactly that schema and nothing more) and attempts a `DROP TABLE IF EXISTS` on a random non-existent name, which must be denied (error 1142). |

If a variable is missing, the tests **fail**; they never skip.

## Run (only once execution is approved)

```sh
dotnet test tests/BuildMind.Persistence.IntegrationTests/BuildMind.Persistence.IntegrationTests.csproj --configuration Release --logger "trx;LogFileName=persistence-spike.trx"
```

## Objectives

| Test | Objective |
|---|---|
| T01–T02 | Connectivity (server version 8.4) and DbContext initialisation |
| T03 | Migration generation: model diff turned into SQL, the same pipeline `dotnet ef` uses |
| T04–T06 | Apply the migration, roll back to empty, reapply, and run the idempotent script twice |
| T07–T09 | Insert/read, update with revision increment, delete |
| T10–T12 | Foreign-key and unique constraints, and translation of duplicate errors (constraint name kept, values dropped) |
| T13–T14 | `TransactionRunner` commit and rollback |
| T15–T16 | Optimistic concurrency, and a conditional revision update with exactly one winner among 8 concurrent writers |
| T17–T20 | GUID, `DECIMAL(18,4)`, UTC `DateTimeOffset` to the microsecond, and JSON semantic round-trips |
| LP | Least privilege of the runtime account |
| SQL | Owner-scoped query uses `ix_spike_items_owner_id` (checked with `EXPLAIN`) |

Column types the provider chooses (GUID, timestamp, decimal) and whether JSON comes back byte-identical are written to the test output as evidence.

**Known limitations:**
- The spike migration is hand-written and has no model snapshot, so `PendingModelChangesWarning` is suppressed for the spike context only.
- T03 proves that model-to-SQL generation works with the provider, and T04–T06 prove that a migration applies, rolls back and reapplies. Together they do **not** prove that CLI-generated migrations (`dotnet ef migrations add`, with a snapshot) work end to end. The proposed CI job adds a `dotnet-ef` tooling smoke check. A real generated migration is first proven with the first agreed feature schema.
- If a test process is killed, its `buildmind_test_<guid>` database may be left behind. Remove leftovers manually; they are identifiable by name and only `bm_test` can create them.
