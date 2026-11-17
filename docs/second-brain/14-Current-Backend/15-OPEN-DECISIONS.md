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
| G18 | M3 merged code uses paths differing from V3 proposals (`python-services/app/plan_editor` vs `plans/editor`; `docs/proposals/m3-plan-editor`), and its C# console contract runner is not in `BuildMind.sln` or CI | Lead + M3 (+ M4 for solution/CI): accept actual paths in 10-OWNERSHIP; decide solution/CI inclusion |
| G19 | PR #8 (BE-M4-D01a) was squash-merged as `183ecf9` with **no recorded review**, although ruleset `BuildMind-Develop-Protection` requires 1 approving + code-owner review | Lead: arrange a retrospective independent review; record outcome (no approval or waiver is assumed) |
| G20 | Implementers not yet assigned for M3 ports `IAiEditProposalPort`, `IValidationFreshnessPolicy` and the composite `IApplicableInputRevisionSource` | Lead + M2/M3/M5/M6/M7 (see 17-SHARED-CONVENTIONS-PROPOSAL §13) |

Until resolution, mark relevant work BLOCKED or PROPOSED; test fixtures must be explicitly labeled. No unsupported compliance, delivery readiness, or live integration claims.

## Status notes — 2026-11-17 (nothing below resolves a decision)
- G01: lead currently holds M4 + M7 (lead decision 2026-11-17); remaining allocation **open**.
- G02: audited 2026-11-17 — see [audit](../17-Progress/07-AUDIT-2026-11-17.md).
- G03, G04, G11, G13: **open**; lead asked to keep EF Core MySQL provider, disposable DB/test account, error format/API prefix, internal service auth and main/develop divergence open until separately reviewed.
- G09: admin module ownership **open**.
- G10: candidate canonical RAG path identified (`C:\BuildMind-PDF-Processor\regulation_retrieval`), not yet explicitly confirmed; RAG repository structure **open** — see [proposal](16-RAG-SOURCE-STRATEGY-PROPOSAL.md).

- 2026-11-17 (D01a): G16 resolved. G17 opened. G03/G04/G09/G10/G11/G13 and final roster remain open.

- 2026-11-17 (develop sync): G13 changed — `main` deleted on the remote; branch `main-→-develop` at former main `d606f00`; `origin/HEAD` dangling locally. Still open. G18 opened.

- 2026-11-17 (lead): default branch intentionally changed to `develop`; the former `main` was accidentally renamed to `main-→-develop`. G13 stays open until the production branch name and protection rules are corrected. G17 and G18 stay open after D01a CI verification.

- 2026-11-17 (BE-M4-D01b, proposals only — nothing resolved):
  - G03: [provider study](18-MYSQL-PROVIDER-STUDY.md) — only stable EF Core 10 MySQL provider found is Oracle `MySql.EntityFrameworkCore 10.0.12+MySQL26.10.0` (GPL-2.0 + FOSS exception, licence review needed; MySQL 8.4 and feature behaviour to be proven in a D02 spike). Pomelo has no EF Core 10 release; EF Core 9 is out of support since 2026-11-10. **Open.**
  - G04: conventions C1–C12 proposed in [17-SHARED-CONVENTIONS-PROPOSAL](17-SHARED-CONVENTIONS-PROPOSAL.md). **Open.** New sub-items A1 (token storage) and A2 (password hasher) listed there.
  - G11: client/timeout/cancellation patterns proposed (I6/I7); internal service authentication, hosting and AI provider still **open**.
  - G13: ruleset on `develop` requires PRs with 1 approving + code-owner review, last-push approval and resolved threads, blocks deletion/force-push/direct updates, but has **no required status checks**; bypass actors not visible without admin access; production branch name pending. **Open.**
  - G17: unchanged, **open** (Smart App Control stays enabled).
  - G18: M3 runner default fixture path confirmed broken by static analysis (resolves to `tests/docs/...`); unmerged M3 `FixtureLocator` would fix it; integration options in §14 of the conventions proposal. **Open** pending M3 agreement.

- 2026-11-18 (BE-M4-D01b STEP 03-A…F, documentation only — **no decision resolved**):
  - G01 final member allocation: **open** (lead holds M4 + M7).
  - G03: re-verified 2026-11-18 — Oracle `10.0.12+MySQL26.10.0` still latest stable, no NuGet advisories; EF Core 11 is `net11.0`-only; Pomelo still 9.0.0. Oracle = research candidate, **BLOCKED** for adoption pending licence review and authorized spike ([study](18-MYSQL-PROVIDER-STUDY.md)). **Open.**
  - G04: decision matrix with states in [17-SHARED-CONVENTIONS-PROPOSAL.md §15](17-SHARED-CONVENTIONS-PROPOSAL.md); C1–C12 direction provisionally accepted for review (lead 2026-11-17), **not approved**. C6 dev origin unverifiable: frontend repository has no Angular project on any branch (2026-11-18). **Open.**
  - G09 admin ownership, G10 RAG source management: **open**.
  - G11: client, timeout and failure contracts specified (§16–§17); internal service auth/transport **open**.
  - G13: unchanged — **open** (required checks, bypass, production branch).
  - G14 Google Sheets automation: **deferred**; must not block development.
  - G17: **open** — Smart App Control stays enabled.
  - G18: revalidated on develop `183ecf9`; PR #9 (`FixtureLocator`) still open and unreviewed. Option B preferred, A fallback, C long-term (§18). **Open** pending M3 agreement.
  - G19: PR #8 retrospective review **outstanding**.
  - G20: ownership recommendations restated (§17); PR #9 would add `ICandidateValidationEvidenceSource` (M6). **Open** — no assignment made.
  - C11b readiness policy: PENDING LEAD APPROVAL (§16).

- 2026-11-18 **lead decision** (STEP 03-G; record in [17-SHARED-CONVENTIONS-PROPOSAL.md §20](17-SHARED-CONVENTIONS-PROPOSAL.md#20-lead-decision-record--2026-11-18)):
  - G04: **partially decided** — C1, C3, C4, C5, C8, C9, C10 APPROVED; C11b APPROVED AS ARCHITECTURAL DESIGN. **G04 stays OPEN** for C2, C5b, C6, C7, C12, `/internal/v1`, feature diagnostics, A1, A2.
  - G03: **CONDITIONAL TESTING APPROVAL** (Oracle candidate for the future D02 spike only). **Open** — adoption requires licence review, verified versions, vulnerability audit, MySQL 8.4/migration/transaction/concurrency/least-privilege tests and explicit lead acceptance.
  - G18: Option B recorded as preferred (A fallback, C long-term). **Open** — no M3 approval claimed.
  - G20: recommendations recorded only. **Open**.
  - Unchanged and **open**: G01, G09, G10, G11, G13, G17, G19. G14 deferred.

- 2026-11-18 (BE-M4-D01c, nothing resolved):
  - **New governance finding (recorded under G19's theme, not a new decision):** PR #10 (BE-M4-D01b docs) was squash-merged as `3f8f439a44b6dd43226e44d94daa94c94c31019a` by the repository owner at 19:58:38 UTC, 37 s after creation and 2 s after its CI run completed ([run 44164246514](https://github.com/Kogulanathan69/BuildMind-Platform/actions/runs/44164246514)), with **no recorded review**. Same pattern as PR #8. Independent review of PR #8 and PR #10 remains outstanding; no approval or waiver recorded.
  - G17: local C# runs blocked different assemblies across rebuilds (Infrastructure, Domain, then the test assembly). **Open**; Smart App Control remains enabled.
  - C8 dependency note: Infrastructure-level options binding would require `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.12 — not installed; binding kept in the API composition root.
  - G01, G03, G04 (C2, C5b, C6, C7, C12, `/internal/v1`, feature diagnostics, A1, A2), G09, G10, G11, G13, G18, G19, G20: **open**. G14 deferred.
