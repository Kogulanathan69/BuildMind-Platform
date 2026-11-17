# Open decisions and release blockers

| ID | Question | Owner / required evidence |
|---|---|---|
| G01 | Actual member-to-slot mapping (lead M4+M7?) and frontend capacity | Lead signoff |
| G02 | Current source, branches, migrations, CI status and uncommitted work | Read-only repository audit |
| G03 | MySQL provider compatibility with EF Core/.NET and exact schema | M4 dependency/test proof |
| G04 | Exact API prefix, names, JSON casing, error envelope, auth/session/reset/email | M4 + M1 + consumers |
| G05 | Survey supported formats, reliable OCR, limits, storage, CRS and tolerances | M2 verified sample coverage |
| G06 | Verified regional rules/authority applicability and source license | M7 expert review |
| G07 | Geometry canonical schema, supported plot/floor/room cases, thresholds and timeout | M6 with M2/M3 |
| G08 | Manual path and upper-floor scope/screen ordering | Product/lead approval |
| G09 | Admin/staff module owner, customer/subscription/payment and architect company portal scope | Product/lead decision |
| G10 | Existing RAG code path, current retrieval API, real evidence tests | M7 source audit |
| G11 | Service transport/auth, hosting, queue/job persistence, retention, AI provider and budget | M4/M6/M7 |
| G12 | Report renderer, sharing channels, retention, recipient authority | M5 approval |
| G13 | Actual CI gates/main branch protection/names | Lead GitHub review |
| G14 | Google Sheets automation alternative setup | Optional; do not block code |
| G15 | Performance targets, release support envelope, deployment and recovery | QA/product signoff |
| G16 | `python-services/app/geometry`, `plans`, `validation` READMEs still state whole-module M6 ownership; V3 splits geometry/land (M2), plans/editor (M3), generation/validation (M6) | **RESOLVED 2026-11-17** — lead approved; geometry/plans/validation READMEs reconciled (BE-M7-D01) |
| G17 | Smart App Control on the lead's workstation blocks loading unsigned freshly built C# assemblies (test host and API run); local C# test execution and API smoke check blocked | Device owner/lead: choose a policy-compliant execution path (e.g. CI on Linux, separate dev environment, approved code-signing); no bypass |

Until resolution, mark relevant work BLOCKED or PROPOSED; test fixtures must be explicitly labeled. No unsupported compliance, delivery readiness, or live integration claims.

## Status notes — 2026-11-17 (nothing below resolves a decision)
- G01: lead currently holds M4 + M7 (lead decision 2026-11-17); remaining allocation **open**.
- G02: audited 2026-11-17 — see [audit](../17-Progress/07-AUDIT-2026-11-17.md).
- G03, G04, G11, G13: **open**; lead asked to keep EF Core MySQL provider, disposable DB/test account, error format/API prefix, internal service auth and main/develop divergence open until separately reviewed.
- G09: admin module ownership **open**.
- G10: candidate canonical RAG path identified (`C:\BuildMind-PDF-Processor\regulation_retrieval`), not yet explicitly confirmed; RAG repository structure **open** — see [proposal](16-RAG-SOURCE-STRATEGY-PROPOSAL.md).

- 2026-11-17 (D01a): G16 resolved. G17 opened. G03/G04/G09/G10/G11/G13 and final roster remain open.
