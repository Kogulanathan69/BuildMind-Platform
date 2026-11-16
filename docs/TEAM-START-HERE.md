# Team start — authoritative working guide

## Seven-member alignment

| Member | Frontend/Backend ownership | Protected folders |
| --- | --- | --- |
| M1 | Angular auth/dashboard integration | Frontend repository, not this backend repository |
| M2 | Angular survey/land/requirements | Frontend repository |
| M3 | Angular 2D editor/plan viewer | Frontend repository |
| M4 | C# API foundation, identity, project CRUD, DbContext/migrations integration | `src/BuildMind.API`, `src/BuildMind.Domain/Entities`, `src/BuildMind.Infrastructure/Persistence` |
| M5 | C# application use cases: requirements, plans, versions, report, architect requests | `src/BuildMind.Application/Features`, feature-specific contracts |
| M6 | Python FastAPI survey, geometry, plan generation, validation | `python-services/` |
| M7 | Team lead, RAG, docs governance, final PR/merge | `rag-service/`, `docs/second-brain/13-Implementation-Blueprint/` |

File owner is responsible for writing or integrating edits. Other members request changes through a PR comment/issue; avoid concurrent modification of shared files. M4 is integration owner for `Program.cs`, dependency injection, C# project files and DB migrations. M6 owns `python-services/app/main.py`. M7 owns RAG contracts; coordinate interface changes with M6. M1–M3 never commit their Angular source into this backend repo.

## Architecture connection

Angular (separate FE repo) → `BuildMind.API` → `BuildMind.Application` → `BuildMind.Domain`.
`BuildMind.Infrastructure` implements Application's persistence/external adapter interfaces; API composes DI. Python/RAG services are private backend integrations accessed via server-side adapters, never direct public client calls.

## Merge policy

1. Start from latest `origin/develop`.
2. Branch e.g. `feature/m4-auth-register`, `feature/m5-plan-versioning`, `feature/m6-survey-parser`, `feature/m7-rag-retrieval`.
3. Work only in your assigned folders; schema/API changes require a contract issue and paired FE member acknowledgment.
4. Run tests, post evidence and contract changes in PR.
5. Only M7/owner resolves conflicts and merges approved PRs; owner-only merge must be enforced and tested with a non-admin account. CODEOWNERS by itself does not grant exclusive merge control.
6. Never push directly to `main` or `develop`; never enable branch bypass for ordinary developers.

## Definition of done

PR diff reviewed; tests pass; no leaked credentials, PDFs, personal data or compiled artifacts; contracts updated; ownership acknowledged; Team Leader merge.

## Day-1 handoff

M4: build/API skeleton, DI, project structure. M5: Application feature contracts and DTOs without editing M4 files. M6: Python health and module shells. M7: second-brain reference, scope reconciliation, RAG indexing design (no sensitive data in GitHub). FE members agree request/response contracts separately in frontend repo.
