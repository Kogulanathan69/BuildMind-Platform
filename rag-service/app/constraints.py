"""Versioned, human-reviewed regulatory constraint provider (M7; constraints contract v0, PENDING M6 + lead approval).

Principles (doc 29):
- Constraints come ONLY from a versioned, human-reviewed table loaded at start-up. Retrieval results are never
  converted into constraints: this module has no dependency on the search path.
- Only HUMAN_REVIEWED constraints can yield VERIFIED. UNREVIEWED ones are returned as UNVERIFIED (warnings only);
  REJECTED ones are never returned. Contradicting reviewed values give CONFLICTING. Absence gives MISSING, never "no
  limit".
- Every constraint cites an approved source passage that must reconstruct EXACTLY from the hash-verified corpus at
  load time, otherwise the table is refused.
- Jurisdiction is exact. Applicability conditions are returned as data for M6's deterministic validator, which stays
  authoritative. Legal currency is never claimed.
- Evidence packs are bound to the exact project, land and requirement revisions supplied by the caller, and are
  identified by a deterministic SHA-256 over their full content.
"""
import hashlib
import json
from datetime import date
from typing import Annotated, Any, Callable, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, StrictInt, StringConstraints, model_validator

CONSTRAINTS_CONTRACT_VERSION = "constraints-0"

Kind = Literal[
    "REAR_SPACE_MIN", "SIDE_SPACE_MIN", "FRONT_SETBACK_MIN", "BUILDING_HEIGHT_MAX",
    "PLOT_COVERAGE_MAX", "FLOOR_AREA_RATIO_MAX", "PARKING_SPACES_MIN",
]
Unit = Literal["m", "percent", "ratio", "count", "m2"]
# kind -> (required comparator, required unit, (minimum, maximum) plausible value range)
KIND_RULES: dict[str, tuple[str, str, tuple[float, float]]] = {
    "REAR_SPACE_MIN": (">=", "m", (0, 100)),
    "SIDE_SPACE_MIN": (">=", "m", (0, 100)),
    "FRONT_SETBACK_MIN": (">=", "m", (0, 100)),
    "BUILDING_HEIGHT_MAX": ("<=", "m", (0, 1000)),
    "PLOT_COVERAGE_MAX": ("<=", "percent", (0, 100)),
    "FLOOR_AREA_RATIO_MAX": ("<=", "ratio", (0, 100)),
    "PARKING_SPACES_MIN": (">=", "count", (0, 1000)),
}
CONDITION_UNITS = {"building_height_m": "m", "plot_area_m2": "m2", "floors": "count", "road_width_m": "m"}

DocumentId = Annotated[str, StringConstraints(pattern=r"^[a-f0-9]{64}$")]
ConstraintId = Annotated[str, StringConstraints(pattern=r"^[a-z0-9][a-z0-9._-]{2,63}$")]
Finite = Annotated[float, Field(allow_inf_nan=False)]


class _Strict(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Condition(_Strict):
    field: Literal["building_height_m", "plot_area_m2", "floors", "road_width_m"]
    comparator: Literal["<", "<=", ">", ">=", "=="]
    value: Finite
    unit: Unit

    @model_validator(mode="after")
    def _unit_matches_field(self):
        if CONDITION_UNITS[self.field] != self.unit:
            raise ValueError("condition unit does not match its field")
        return self


class Source(_Strict):
    document_id: DocumentId
    page: StrictInt = Field(ge=1)
    start_char: StrictInt = Field(ge=0)
    end_char: StrictInt = Field(ge=1)
    excerpt: str = Field(min_length=1, max_length=4000)

    @model_validator(mode="after")
    def _ordered(self):
        if self.end_char <= self.start_char:
            raise ValueError("end_char must follow start_char")
        return self


class Constraint(_Strict):
    constraint_id: ConstraintId
    jurisdiction: str = Field(min_length=1, max_length=200)
    kind: Kind
    comparator: Literal[">=", "<="]
    value: Finite
    unit: Unit
    applies_to: Literal["residential", "any"]
    conditions: list[Condition] = Field(default_factory=list, max_length=5)
    review_status: Literal["HUMAN_REVIEWED", "UNREVIEWED", "REJECTED"]
    reviewer_role: str | None = Field(default=None, max_length=100)
    reviewed_at: date | None = None
    source: Source

    @model_validator(mode="after")
    def _consistent(self):
        comparator, unit, (low, high) = KIND_RULES[self.kind]
        if self.comparator != comparator or self.unit != unit:
            raise ValueError("comparator or unit does not match the constraint kind")
        if not low <= self.value <= high:
            raise ValueError("value outside the plausible range for the kind")
        if self.review_status == "HUMAN_REVIEWED" and (not self.reviewer_role or self.reviewed_at is None):
            raise ValueError("a human-reviewed constraint needs reviewer_role and reviewed_at")
        return self


class ConstraintTable(_Strict):
    contract_version: Literal["constraints-0"]
    table_version: str = Field(pattern=r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
    synthetic: bool
    constraints: list[Constraint] = Field(max_length=10000)


class Binding(_Strict):
    """Exact revisions the evidence pack is bound to (supplied by the C# orchestration, never inferred)."""

    project_id: UUID
    project_revision: StrictInt = Field(ge=1)
    land_revision_id: UUID
    requirement_revision_id: UUID


class ConstraintsRequest(_Strict):
    jurisdiction: str = Field(min_length=1, max_length=200)
    kinds: list[Kind] = Field(min_length=1, max_length=len(KIND_RULES))
    building_use: Literal["residential"] | None = None
    binding: Binding

    @model_validator(mode="after")
    def _unique_kinds(self):
        if len(set(self.kinds)) != len(self.kinds):
            raise ValueError("kinds must be unique")
        return self


def _signature(c: Constraint) -> tuple:
    return (c.comparator, c.value, c.unit)


def _scope(c: Constraint) -> str:
    return json.dumps([c.applies_to, [x.model_dump() for x in c.conditions]], sort_keys=True)


class ConstraintProvider:
    def __init__(self, table: dict[str, Any], page_text: Callable[[str, int], str | None]):
        self.table = ConstraintTable.model_validate(table)
        ids = [c.constraint_id for c in self.table.constraints]
        if len(set(ids)) != len(ids):
            raise ValueError("duplicate constraint_id in table")
        for c in self.table.constraints:
            text = page_text(c.source.document_id, c.source.page)
            if text is None or c.source.end_char > len(text) or text[c.source.start_char:c.source.end_char] != c.source.excerpt:
                raise ValueError(f"constraint {c.constraint_id}: source excerpt does not reconstruct from the corpus")

    def lookup(self, request: ConstraintsRequest) -> dict[str, Any]:
        known_jurisdiction = any(c.jurisdiction == request.jurisdiction for c in self.table.constraints)
        results = []
        for kind in request.kinds:
            candidates = [
                c for c in self.table.constraints
                if c.jurisdiction == request.jurisdiction and c.kind == kind and c.review_status != "REJECTED"
                and (request.building_use is None or c.applies_to in (request.building_use, "any"))
            ]
            reviewed = [c for c in candidates if c.review_status == "HUMAN_REVIEWED"]
            if reviewed:
                scopes: dict[str, set] = {}
                for c in reviewed:
                    scopes.setdefault(_scope(c), set()).add(_signature(c))
                status = "CONFLICTING" if any(len(s) > 1 for s in scopes.values()) else "VERIFIED"
            elif candidates:
                status = "UNVERIFIED"
            else:
                status = "MISSING"
            results.append({
                "kind": kind,
                "status": status,
                "constraints": [self._public(c) for c in sorted(candidates, key=lambda c: c.constraint_id)],
            })

        warnings = ["Legal currency and amendments are not verified.",
                    "Only HUMAN_REVIEWED constraints may block; UNVERIFIED, CONFLICTING and MISSING kinds require review."]
        if self.table.synthetic:
            warnings.append("SYNTHETIC_TABLE: test values only, not real regulations.")
        if not known_jurisdiction:
            warnings.append("NO_CONSTRAINTS_FOR_JURISDICTION")
        pack = {
            "contract_version": CONSTRAINTS_CONTRACT_VERSION,
            "table_version": self.table.table_version,
            "synthetic": self.table.synthetic,
            "jurisdiction": request.jurisdiction,
            "building_use": request.building_use,
            "binding": json.loads(request.binding.model_dump_json()),
            "kinds": results,
            "overall_status": "COMPLETE" if all(r["status"] == "VERIFIED" for r in results) else "INCOMPLETE",
            "legal_status_verified": False,
        }
        canonical = json.dumps(pack, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        return pack | {"evidence_pack_id": hashlib.sha256(canonical.encode("ascii")).hexdigest(), "warnings": warnings}

    @staticmethod
    def _public(c: Constraint) -> dict[str, Any]:
        data = json.loads(c.model_dump_json())
        data.pop("jurisdiction")
        return data
