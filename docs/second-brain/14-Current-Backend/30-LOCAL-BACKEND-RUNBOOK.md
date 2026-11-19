# Local backend startup and integration runbook (MVP, development only)

- **Scope:** development and testing on a developer machine. **Not a production deployment guide**: production hosting, TLS termination, authentication and G11 are not decided.
- **Secrets:** use **environment variables or user secrets only**. Never commit values.

## 0. Prerequisites
| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.x (CI pins it with a job-local `global.json`) | `dotnet --info` |
| Python | 3.13 | One virtual environment per service is recommended |
| MySQL | 8.4 LTS | Only needed with `Database__Enabled=true` |
| Approved RAG corpus | The STEP 4 corpus folder (PDFs, extracted records, manifest, index) | Outside the repository; never commit it |

**Windows Smart App Control (G17):** on the lead's machine it blocks locally built .NET assemblies. The API fails with *"An Application Control policy has blocked this file (0x800711C7)"*, verified 2026-11-19.
- **Do not bypass it.**
- Run the C# API and tests on a machine without this policy, in Linux CI, or in an approved Linux environment.
- The Python services run normally.

## 1. Build
```powershell
dotnet restore BuildMind.sln
dotnet build BuildMind.sln -c Release
```

## 2. python-services (M2/M3/M6 FastAPI)
```powershell
cd python-services
python -m venv .venv; .\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -B -m uvicorn app.main:app --host 127.0.0.1 --port 8000
# check: GET http://127.0.0.1:8000/health -> {"service":"buildmind-python","status":"ok"}
```

## 3. rag-service (M7, evidence contract v1)
```powershell
cd rag-service
python -m pip install -r requirements-dev.txt     # or reuse a venv with the same pins
$env:BUILDMIND_RAG_CORPUS_ROOT = '<corpus root containing input_pdfs\ and output\documents\>'
$env:BUILDMIND_RAG_MANIFEST    = '<...>\output\regulation_retrieval_step4\approved_corpus_manifest.json'
$env:BUILDMIND_RAG_INDEX       = '<...>\output\regulation_retrieval_step4\multi_document_index.json'
python -B -m uvicorn app.main:app --host 127.0.0.1 --port 8100
# check: GET http://127.0.0.1:8100/health -> {"service":"buildmind-rag","status":"ok","contract_version":"1","corpus_loaded":true}
```
- **Corpus integrity:** start-up fails fast if any approved PDF or record hash doesn't match.
- **Without the corpus variables**, the service runs with `corpus_loaded:false`: search returns 503 and API readiness reports RAG `Degraded/unavailable`.

## 4. MySQL (optional)
1. Create a database and a **non-root, TLS-only** runtime account.
2. Apply the reviewed ranged migration script (Option B; idempotent scripts are a provider limitation). This is the same invocation CI uses. The design-time factory reads `BUILDMIND_MIGRATIONS_CONNECTION`; script generation needs no connection, but the variable must be set:
   ```powershell
   dotnet tool restore
   $env:BUILDMIND_MIGRATIONS_CONNECTION = 'Server=127.0.0.1;Port=3306;Database=buildmind_dev;User ID=<admin>;SslMode=Required'  # no password needed to generate
   dotnet tool run dotnet-ef migrations script 0 --configuration Release `
     --project src/BuildMind.Infrastructure --startup-project src/BuildMind.API `
     --context BuildMindDbContext --output buildmind-from-0.sql
   ```
3. Review the script, then apply it with your MySQL client as an administrator.
4. Remember the TLS policy: `SslMode=VerifyFull` is required in every non-Development environment and for any non-loopback server. `Required` is accepted only for loopback in Development.

## 5. C# API
```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
$env:Services__Python__Enabled = 'true'; $env:Services__Python__BaseUrl = 'http://127.0.0.1:8000/'
$env:Services__Rag__Enabled = 'true';    $env:Services__Rag__BaseUrl    = 'http://127.0.0.1:8100/'
# optional database:
# $env:Database__Enabled = 'true'; $env:ConnectionStrings__BuildMind = '<from user secrets / environment>'
dotnet run --project src/BuildMind.API -c Release
```

**Checks:**
- **Liveness:** `GET /health` → `{"service":"buildmind-api","status":"ok"}`.
- **Readiness:** `GET /health/ready`.
  - MySQL is mandatory: not configured or unreachable → **503**.
  - Python and RAG are Degraded-only (never 503). RAG is Healthy only with the right identity, contract v1 and a loaded corpus.

## 6. Tests
| Suite | Command | Where |
|---|---|---|
| C# (all solution tests) | `dotnet test BuildMind.sln -c Release` | CI (blocked locally by G17) |
| MySQL + live Python integration | `dotnet test tests/BuildMind.Persistence.IntegrationTests` with `BUILDMIND_TEST_MYSQL`, `BUILDMIND_TEST_MYSQL_RUNTIME`, `BUILDMIND_TEST_PYTHON_URL` | CI `database-compatibility` |
| python-services | `cd python-services; python -m pytest -q` | Local + CI |
| rag-service (synthetic corpus) | `cd rag-service; python -B -m pytest -q -p no:cacheprovider` | Local + CI |
| rag-service against the real corpus | `cd rag-service; python -B -m pytest -p no:cacheprovider tests_local` (the 3 corpus variables set) | Local only (corpus never in CI) |

## 7. Ports used in this runbook
| Service | Port |
|---|---|
| API | 8080 |
| python-services | 8000 |
| rag-service | 8100 |

All bind to **127.0.0.1**. Do not expose the Python or RAG services publicly: they have no authentication yet (G11).
