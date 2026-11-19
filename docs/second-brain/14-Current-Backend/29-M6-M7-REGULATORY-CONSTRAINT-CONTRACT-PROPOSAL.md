# M6–M7 regulatory constraint contract: PROPOSAL for team coordination

- **Status:** **PROPOSED, NOT APPROVED, NOT IMPLEMENTED** (2026-11-19).
- **Owners:** M7 (provider), M6 (consumer and authoritative validator). Decision: lead.
- No M6 code exists on any branch. Nothing here may be implemented until **M6, M7 and the lead** approve it.

## 1. Why a separate contract
- **Evidence contract v1** (implemented) returns *passages*: cited text plus an evidence status. It is advisory research evidence and must never decide a plan.
- **M6's plan** (task plan days 3–5) expects "M7 sourced rules / rule constraints / rule provenance" for its **deterministic** feasibility and geometry validation.
- **The gap:** turning a passage into an enforceable number (for example "rear space ≥ 3 m") is a **human-review** step. It can't come from BM25 or from any AI output.

## 2. Principles (non-negotiable)
1. **M6's deterministic validator is authoritative.** M7 never returns pass/fail for a plan.
2. **Only human-reviewed constraints can block.** Unreviewed constraints may only produce warnings.
3. **Every constraint carries full provenance** (document, page, character offsets, excerpt) that reconstructs from the approved corpus.
4. **A missing constraint is never treated as "no limit".** It yields `NEEDS_REVIEW` / `INSUFFICIENT_EVIDENCE`, never "feasible".
5. **Jurisdiction is exact.** There's no inference from parcel location; parcel applicability stays a human or M2 decision.
6. **Legal currency is never claimed:** `legal_status_verified=false` until G06 is resolved.

## 3. Proposed shape (v0, for discussion)
```json
{
  "contract_version": "constraints-0",
  "jurisdiction": "Colombo Municipal Council planning area",
  "constraints": [
    {
      "constraint_id": "sha256(...)",
      "kind": "REAR_SPACE_MIN",
      "comparator": ">=",
      "value": 3.0,
      "unit": "m",
      "conditions": [{"field": "building_height_m", "comparator": "<=", "value": 15.0}],
      "applies_to": "residential",
      "review_status": "HUMAN_REVIEWED",
      "reviewed_by_role": "domain_reviewer",
      "reviewed_at": "2026-11-20",
      "source": {"document_id": "<64 hex>", "page": 358, "start_char": 789, "end_char": 1269, "excerpt": "..."},
      "legal_status_verified": false
    }
  ],
  "coverage": {"requested_kinds": ["REAR_SPACE_MIN", "PLOT_COVERAGE_MAX"], "missing_kinds": ["PLOT_COVERAGE_MAX"]},
  "warnings": ["Legal currency and amendments are not verified."]
}
```

**Value sets:**
- **Kinds:** a closed enum agreed with M6, starting from what M6's validator can actually check: `REAR_SPACE_MIN`, `SIDE_SPACE_MIN`, `FRONT_SETBACK_MIN`, `PLOT_COVERAGE_MAX`, `FLOOR_AREA_RATIO_MAX`, `BUILDING_HEIGHT_MAX`, `PARKING_SPACES_MIN`. New kinds need a contract version change.
- **Review status:** `HUMAN_REVIEWED` (may block), `UNREVIEWED` (warning only), `REJECTED` (never returned).
- **M6 outcome per kind:**
  - reviewed and satisfied → `PASS`;
  - reviewed and violated → `FAIL` (blocking);
  - unreviewed → `WARNING`;
  - missing → `NEEDS_REVIEW`.

  The overall plan is never `FEASIBLE` while any requested kind is `NEEDS_REVIEW`.

## 4. Where the constraints come from (proposed)
- An **offline, version-controlled, reviewed constraint table** maintained by M7 with a domain reviewer. Each row references an approved passage by citation.
- The citation is re-verified against the hashed corpus when the service starts (as the STEP 6 assessor already does).
- Retrieval (contract v1) **helps reviewers find** candidate passages. It doesn't create constraints automatically.

## 5. Interfaces (proposed, not created)
| Side | Interface | Notes |
|---|---|---|
| Python (rag-service) | `POST /internal/v1/regulations/constraints` with `{jurisdiction, kinds[]}` | Read-only; returns only `HUMAN_REVIEWED` and `UNREVIEWED` rows plus `missing_kinds` |
| C# | `IRegulatoryConstraintSource` (Application port, M7) | Consumed by M6 generation and feasibility orchestration |
| M6 Python | The validator receives constraints as **input data** | It never calls M7 directly (single orchestration path through C#) |

## 6. Open questions for the team
1. Which kinds can M6's validator check first (day-3/4 scope)?
2. Who is the domain reviewer, and what review evidence is needed (G06)?
3. How are the "conditions" (height, plot size, zone) supplied: from M2 land data or M5 requirements?
4. Is the Gazette (broad national scope) applicable alongside a council plan, and how are precedence and conflicts handled? Proposal: never merged automatically; returned separately as `NEEDS_REVIEW`.
5. Storage of the reviewed table (repo file vs database), and its versioning.

## 7. Approval checklist
- [ ] M6 agrees the kinds and outcome semantics.
- [ ] M7 agrees the review workflow and provenance.
- [ ] Lead approves the contract version, and where the reviewed table lives.
- [ ] Domain reviewer assigned (G06).
