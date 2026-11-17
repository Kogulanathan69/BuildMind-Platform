# Runtime and Clean Architecture

```mermaid
flowchart TD
  UI[Angular web app] --> API[ASP.NET Core public API]
  API --> APP[Application use cases]
  APP --> DOMAIN[Domain rules]
  API --> INFRA[Infrastructure adapters]
  INFRA --> DB[(MySQL)]
  INFRA --> FILES[Private artifact storage]
  INFRA --> PY[Internal FastAPI]
  PY --> SUR[Survey and land M2]
  PY --> EDIT[Editor transformations M3]
  PY --> GEN[Generation and validation M6]
  GEN --> RAG[RAG evidence M7]
  API --> RAG
```

Compile-time: Domain has no outward dependency. Application -> Domain. Infrastructure -> Application/Domain as needed. API -> Application/Infrastructure composition root. No cycles. API endpoints translate transport, Application orchestrates use cases, Domain protects invariants, Infrastructure implements ports. Python is not a separate public customer backend. Internal routes protected by network and authenticated service identity. C# owns customer identity, state, transactions and persistence. Computational Python functions operate on explicit snapshots and return structured results, not direct changes to MySQL customer state.

Existing starter layout historically observed: `src/BuildMind.API`, `src/BuildMind.Application`, `src/BuildMind.Domain`, `src/BuildMind.Infrastructure`, `python-services/app`, `rag-service`, `tests`, `docs`. Inspect actual current tree. Do not create duplicate `backend/` wrapper or another FastAPI application. Prefer vertical feature folders under Application and separate endpoint classes. M4 is composition-root/DbContext/migration custodian; proposed M6 Python bootstrap custodian; M7 owns RAG and lead coordination. See [ownership](10-OWNERSHIP.md).
