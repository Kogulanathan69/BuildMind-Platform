# M4 + M7 MVP status and cross-member dependencies (2026-11-19, accelerated delivery)

**Not production-ready.** "Runtime-verified" means executed in CI or in a recorded local run. "Compiled" means not executed yet: Smart App Control (G17) blocks C# test execution locally.

## 1. M4 (backend infrastructure)
| Task | MVP state | Evidence | Open |
|---|---|---|---|
| D01 foundation | Runtime-verified (build, `/health`, layering, Problem Details, config validation) | CI #52 | C6 CORS, C7 OpenAPI, A1/A2 |
| D02 MySQL/identity | Runtime-verified (provider, DbContext, migration, least-privilege CI) | CI #52 | JWT (A1/A2 + M1), production database account policy |
| D03 schema | Runtime-verified (4 tables, migration, requirement repository) | CI #52 | M1 factories; M2/M3/M6 entities |
| D04 errors/security | Runtime-verified (error mapping, log safety, readiness, headers) | CI #52 | CORS, auth policies, request/rate limits |
| D05 C#↔Python | Runtime-verified (hardened client, bulkhead, live smoke 7/7) | CI #52 | G11 auth; M2 storage adapter; job dedup |
| D06 concurrency | Runtime-verified (approved failure mapping, live concurrency 4/4) | CI #52 | Idempotency (C2/C5b) |
| D07 QA | **Final QA OPEN**: MySql.Data TLS race (run #55); open-gate fix in **PR #30** | Run #59 attempts 1–2 **green by public metadata; artifacts not read** (0/5 verified) | 5 verified green runs; **R-1, R-3 unresolved production blockers** (doc 27); independent review |

## 2. M7 (RAG and leadership/QA)
| Task | MVP state | Evidence | Open |
|---|---|---|---|
| D01 audit | Done (2026-11-17) | Audit doc | — |
| D02 corpus/tests | **Done.** Existing suite 93/93 run read-only; 3 approved docs / 2,629 chunks | EV-20261119-11 | Legal currency (G06) |
| D03 contract | **Done.** Evidence contract v1 lead-approved and implemented | EV-20261119-11 | — |
| D04 integration | **Implemented** (branch `feature/m7-rag-retrieval-service`): rag-service FastAPI; C# `IRegulationEvidenceSource`/`RagServiceClient`; **real RAG readiness** (identity, contract version, corpus, bounded, single-flight, never Unhealthy) | rag-service 44/44 local; real corpus 6/6; real loopback end-to-end; C# compiled | First CI run (PR not yet opened); G11 auth |
| D05 M6 evidence | **BLOCKED: no M6 code or contract exists** on any branch (no `feature/m6-*`). M6's plan expects "M7 sourced rules/constraints", which needs a new M6+M7 contract (structured, reviewed constraints, not passages). Nothing was invented | Branch and port audit, 2026-11-19 | M6 implementation; joint contract (lead approval) |
| D06 cross-service QA | Partial: per-service suites and the shared contract example; no M6 end-to-end | — | M6 |
| D07 go/no-go | **No-go for production**; demo-capable for retrieval only | This document | Independent review |

**Deterministic validation stays authoritative.** Retrieved passages are advisory evidence only:
- `legal_status_verified` and `answer_generated` are always false;
- no code path turns a passage into a pass/fail plan decision;
- M6's authoritative validator (`IPlanValidationEvidenceVerifier`, M6-owned) is unchanged and unimplemented by M7.

## 3. Consolidated cross-member dependencies
| # | Needed from | What | Blocks |
|---|---|---|---|
| 1 | **M6** | Any generation/feasibility code; a joint "sourced regulatory constraint" contract with M7 | M7-D05/D06, M4 job persistence |
| 2 | **M1** | `User`/`Project` rehydration factories; auth/token service (A1/A2) | M4-D02 JWT, D03 repositories, D04 auth policies |
| 3 | **M2** | Storage contract, upload limits, land/survey entities | M4-D05 storage adapter, D04 request limits, D03 schema |
| 4 | **M3** | Plan/job contracts (PR #31 draft); `IAiEditProposalPort` and `IValidationFreshnessPolicy` implementers (G20) | M4 job/version keys; idempotency |
| 5 | **M5** | Report snapshot contract | M4-D06 integration |
| 6 | **Lead** | G11 internal auth/transport/hosting; C6; C7; C2/C5b; R-3 decision (doc 27); production database account policy; Actions SHA pinning | Production readiness |
| 7 | **Lead (GitHub)** | Authenticated download of CI artifacts (run #59 attempts) | M4 5-run verification |
| 8 | **Independent reviewers** | PRs #8–#30, the M7 PR (G19) | Every "Done" |
| 9 | **Domain/legal (G06)** | Legal currency and amendment status of the 3 sources | Any regulatory claim |

## 4. Verification run for this phase
See the evidence register (EV-20261119-12). Local results:
- rag-service 44/44;
- python-services 54/54;
- Release build 0 warnings;
- no vulnerable packages.

C# test execution happens only in CI.
