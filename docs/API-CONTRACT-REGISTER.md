# API contract register — summary (Second Brain V3.0)

> **Reconciled 2026-11-17 (task BE-M7-D01).** The detailed proposed register is [05-API-CONTRACTS](second-brain/14-Current-Backend/05-API-CONTRACTS.md); this file is a summary and implementation-status tracker. **No contract below is frozen or approved.** API prefix (`/api/v1` proposed), JSON casing and error envelope remain open (G04). The previous V2-era table is preserved under [Historical](#historical-superseded--initial-register).

Do not consider an endpoint implemented until code, integration tests and PR evidence are linked.

## Areas, owners and status

| Area | Contract owner (V3) | Service | Contract status | Implementation status |
| --- | --- | --- | --- | --- |
| Auth/session, users, projects | M1 (M4: shared auth/error infrastructure) | ASP.NET | Proposed | Not started |
| Survey upload/extraction, land/location | M2 | ASP.NET → Python (internal) | Proposed | Not started |
| Requirements, dashboard, design method | M5 | ASP.NET | Proposed | Not started |
| Generation jobs, three 2D alternatives, selection | M6 | ASP.NET → Python (internal) | Proposed | Not started |
| Plan editor, edit validation, AI edit proposals, versions, confirmation | M3 (M6 authoritative validation) | ASP.NET → Python (internal) | Proposed | Not started |
| Preliminary report, architect requests | M5 | ASP.NET | Proposed | Not started |
| Regulatory evidence retrieval / citations | M7 (M4: shared C# client infrastructure) | ASP.NET → RAG (internal) | Candidate route `POST /regulations/retrieve`; assistant route TBD | Not started. Existing RAG engine exposes a Python function, **no HTTP route** (see [08-RAG](second-brain/14-Current-Backend/08-RAG.md)) |
| Health: liveness `GET /health` | M4 | ASP.NET | Proposed (current body shape below) | **Implemented and verified 2026-11-17** |
| Health: readiness `GET /health/ready` | M4 | ASP.NET | Proposed | Not implemented (returns 404, verified 2026-11-17) |
| Python internal `GET /health` | M6 bootstrap custodian | FastAPI (internal) | Starter | **Implemented; pytest 1 passed 2026-11-17** |
| Admin / staff / proposal routes | Unassigned (G09) | ASP.NET | Not defined | Not started |

Frontend consumers are tracked by the frontend repository; this register does not assign them.

## Verified existing endpoints (evidence: [2026-11-17 audit](second-brain/17-Progress/07-AUDIT-2026-11-17.md))

| Service | Route | Observed response | Notes |
| --- | --- | --- | --- |
| ASP.NET `BuildMind.API` | `GET /health` | `200 {"service":"buildmind-api","status":"ok"}` | Liveness only; does not check DB/Python/RAG. No `/api/v1` prefix. |
| FastAPI `python-services` | `GET /health` | `200 {"service":"buildmind-python","status":"ok"}` (via TestClient) | Internal only. |

Out of scope for this register: 3D and construction cost endpoints (excluded by D311), payment/subscription (open, D314).

---

## Historical (superseded) — initial register

> **HISTORICAL — not current.** Preserved from commit `763a9a7`. Conflicts with V3: auth/session and projects are M1 contracts (M4 only provides shared infrastructure); requirements consumer and plan/editor owners changed (M3 editor/versions/confirmation, M6 generation); UI consumer columns referred to V2 frontend roles.

| Area | Contract owner | UI consumer | Service | Status |
| --- | --- | --- | --- | --- |
| Auth/session | M4 | M1 | ASP.NET | Proposed |
| Projects/dashboard | M4 | M1 | ASP.NET | Proposed |
| Survey upload/land details | M4 + M6 | M2 | ASP.NET → Python | Proposed |
| Ground-floor requirements | M5 | M2 | ASP.NET | Proposed |
| Three 2D plan alternatives | M5 + M6 | M3 | ASP.NET → Python | Proposed |
| Room editing/validation | M5 + M6 | M3 | ASP.NET → Python | Proposed |
| Regulation citations | M7 + M6 | M2/M3 via main API | ASP.NET → Python/RAG | Proposed |
| Confirm revision | M5 | M3 | ASP.NET | Proposed |
