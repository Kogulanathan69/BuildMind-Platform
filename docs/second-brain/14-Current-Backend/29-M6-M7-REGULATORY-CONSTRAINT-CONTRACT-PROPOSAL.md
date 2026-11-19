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

## 8. Coordination message (ready to send to M6; lead to send)
> **To M6, from M7 (cc lead).** The M7 regulation retrieval service is ready for review (PR #32). It returns cited passages and an evidence status only, never pass/fail.
>
> For your feasibility and validation work we propose a separate, reviewed **constraint** contract (doc 29): only human-reviewed constraints can block, a missing constraint means `NEEDS_REVIEW`, and your deterministic validator stays authoritative.
>
> Please reply with:
> 1. the constraint kinds your validator can check first;
> 2. where conditions such as height and plot size come from;
> 3. your preferred delivery path (constraints passed to your validator as input through C#).
>
> Nothing will be implemented until M6, M7 and the lead approve.

## 9. Revised technical submission to M6 (2026-11-19, after the M6 review: 2 PASS / 6 PARTIAL / 4 FAIL)
**Status:** constraints contract **v0 IMPLEMENTED on `feature/m7-regulatory-constraints` with synthetic fixtures only**. It still **needs M6 + lead approval** before any real values are loaded or M6 consumes it. No M6-owned code was changed.

### What M6 asked for → what now exists
| M6 review area | Implementation | Evidence |
|---|---|---|
| Versioned, human-reviewed provider | `rag-service/app/constraints.py`: table with `contract_version` `constraints-0`, `table_version`, `synthetic` flag; strict schema (`extra=forbid`, frozen) | `tests/test_constraints.py` |
| Verified numeric values and units | Closed kind enum; each kind fixes its comparator (`>=` MIN, `<=` MAX) and unit (`m`, `percent`, `ratio`, `count`); finite values in plausible ranges | Load-refusal tests (unit, comparator, range, NaN, unknown kind) |
| Source provenance | Every constraint cites document/page/offsets/excerpt; **the table is refused unless every excerpt reconstructs exactly from the hash-verified corpus** | Synthetic refusals; **real-corpus test: synthetic citations are refused against the real corpus** |
| Applicability | `applies_to` (`residential`/`any`) plus structured `conditions` (field/comparator/value/unit) returned as data; **M6 evaluates them** | Condition-scope test (different conditions ≠ conflict) |
| Missing / conflicting / unverified states | Per kind: `VERIFIED` (only HUMAN_REVIEWED), `UNVERIFIED` (unreviewed only), `CONFLICTING` (reviewed values disagree in the same scope), `MISSING` (never "no limit"); `REJECTED` never returned; overall `COMPLETE` only if all VERIFIED | State tests |
| Never auto-verify retrieved passages | The provider has no dependency on retrieval; there is no automatic review state; a search cannot change a pack | `test_retrieval_never_creates_constraints` |
| Binding to exact revisions | Request **requires** `binding{project_id, project_revision, land_revision_id, requirement_revision_id}`; echoed and covered by a deterministic `evidence_pack_id` (SHA-256 of the canonical pack) | Determinism and revision-sensitivity tests; the C# client rejects a pack bound to any other revision |
| Request validation and payload limits | Strict raw JSON before the models: streamed 16 KB limit (also chunked), `application/json` only, duplicate keys/NaN/trailing commas/depth > 32/non-object root → 400; schema → 422; no input echo | `tests/test_strict_json.py` |
| Deterministic validation authoritative | The contract carries data only; M7 never returns pass/fail for a plan | By design; C# port docs |

### Endpoint
`POST /internal/v1/regulations/constraints`. Internal token required; it returns 503 `CONSTRAINTS_UNAVAILABLE` when no table is configured, or refuses to start when the corpus is missing (provenance cannot be verified). Example: `rag-service/contract/constraints_response.v0.example.json` (generated from the real provider; validated by both Python and C# tests).

### C#
`IRegulatoryConstraintSource` → `RagServiceClient.GetConstraintsAsync`. It validates the pack on receipt (binding, kinds, state consistency, units, comparators, no REJECTED, legal flag) → otherwise `MalformedResponse`.

### Still needs M6 + lead decisions
1. **Kind set and units:** is the first set (7 kinds) right for M6's validator?
2. **Condition fields** (`building_height_m`, `plot_area_m2`, `floors`, `road_width_m`): where M6 gets their actual values (M2 land / M5 requirements).
3. **Real values:** who is the domain reviewer (G06), and where does the reviewed table live? **No real regulatory values are in the repository.**
4. **Gazette vs council precedence:** currently exact-jurisdiction only; overlapping scopes are not merged.
