"""Human-reviewed regulatory constraint provider (constraints contract v0; synthetic fixtures only)."""
import copy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.auth import InternalAuth
from app.constraints import ConstraintProvider, ConstraintsRequest
from app.main import create_app
from fastapi.testclient import TestClient
from support import TEST_TOKEN
from synthetic_corpus import NORTH_JURISDICTION, SOUTH_JURISDICTION, SyntheticEngine

ROOT = Path(__file__).resolve().parents[1]
TABLE = json.loads((ROOT / "tests" / "fixtures" / "synthetic_constraints.v0.json").read_text(encoding="utf-8"))
EXAMPLE = ROOT / "contract" / "constraints_response.v0.example.json"
ENDPOINT = "/internal/v1/regulations/constraints"
BINDING = {
    "project_id": "11111111-1111-4111-8111-111111111111",
    "project_revision": 3,
    "land_revision_id": "22222222-2222-4222-8222-222222222222",
    "requirement_revision_id": "33333333-3333-4333-8333-333333333333",
}
ENGINE = SyntheticEngine()


def provider(table=None):
    return ConstraintProvider(copy.deepcopy(table or TABLE), ENGINE.page_text)


def request(**overrides):
    body = {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING} | overrides
    return ConstraintsRequest.model_validate(body)


def by_kind(pack):
    return {k["kind"]: k for k in pack["kinds"]}


def client(constraints):
    from support import AUTH_HEADER
    return TestClient(create_app(ENGINE, InternalAuth.from_tokens([TEST_TOKEN]), constraints), headers={AUTH_HEADER: TEST_TOKEN})


# ---- states -------------------------------------------------------------------------------------------------------

def test_reviewed_constraint_is_verified_with_value_unit_source_and_applicability():
    pack = provider().lookup(request())
    rear = by_kind(pack)["REAR_SPACE_MIN"]

    assert rear["status"] == "VERIFIED" and pack["overall_status"] == "COMPLETE"
    c = rear["constraints"][0]
    assert (c["comparator"], c["value"], c["unit"], c["applies_to"], c["review_status"]) == (">=", 3.0, "m", "residential", "HUMAN_REVIEWED")
    assert ENGINE.page_text(c["source"]["document_id"], c["source"]["page"])[c["source"]["start_char"]:c["source"]["end_char"]] == c["source"]["excerpt"]
    assert pack["legal_status_verified"] is False


def test_unreviewed_constraint_is_returned_only_as_unverified():
    side = by_kind(provider().lookup(request(kinds=["SIDE_SPACE_MIN"])))["SIDE_SPACE_MIN"]

    assert side["status"] == "UNVERIFIED"
    assert side["constraints"][0]["review_status"] == "UNREVIEWED"


def test_missing_kind_is_missing_never_unlimited_and_pack_is_incomplete():
    pack = provider().lookup(request(kinds=["REAR_SPACE_MIN", "FLOOR_AREA_RATIO_MAX"]))

    assert by_kind(pack)["FLOOR_AREA_RATIO_MAX"] == {"kind": "FLOOR_AREA_RATIO_MAX", "status": "MISSING", "constraints": []}
    assert pack["overall_status"] == "INCOMPLETE"


def test_rejected_constraints_are_never_returned():
    parking = by_kind(provider().lookup(request(jurisdiction=SOUTH_JURISDICTION, kinds=["PARKING_SPACES_MIN"])))["PARKING_SPACES_MIN"]

    assert parking == {"kind": "PARKING_SPACES_MIN", "status": "MISSING", "constraints": []}


def test_contradicting_reviewed_values_in_the_same_scope_are_conflicting():
    table = copy.deepcopy(TABLE)
    clone = copy.deepcopy(next(c for c in table["constraints"] if c["constraint_id"] == "syn.north.height"))
    table["constraints"].append(clone | {"constraint_id": "syn.north.height-2", "value": 12})

    height = by_kind(provider(table).lookup(request(kinds=["BUILDING_HEIGHT_MAX"])))["BUILDING_HEIGHT_MAX"]

    assert height["status"] == "CONFLICTING" and len(height["constraints"]) == 2


def test_different_applicability_conditions_are_not_a_conflict():
    table = copy.deepcopy(TABLE)
    clone = copy.deepcopy(next(c for c in table["constraints"] if c["constraint_id"] == "syn.north.height"))
    table["constraints"].append(clone | {"constraint_id": "syn.north.height-wide-road", "value": 20,
                                         "conditions": [{"field": "road_width_m", "comparator": ">=", "value": 9, "unit": "m"}]})

    height = by_kind(provider(table).lookup(request(kinds=["BUILDING_HEIGHT_MAX"])))["BUILDING_HEIGHT_MAX"]

    assert height["status"] == "VERIFIED" and len(height["constraints"]) == 2  # M6 evaluates the conditions


def test_jurisdiction_is_exact_and_unknown_jurisdictions_are_reported():
    pack = provider().lookup(request(jurisdiction="Synthetic North Council"))  # not the exact label

    assert by_kind(pack)["REAR_SPACE_MIN"]["status"] == "MISSING"
    assert "NO_CONSTRAINTS_FOR_JURISDICTION" in pack["warnings"]


def test_synthetic_tables_are_always_flagged():
    pack = provider().lookup(request())

    assert pack["synthetic"] is True and any(w.startswith("SYNTHETIC_TABLE") for w in pack["warnings"])


# ---- binding / evidence pack ----------------------------------------------------------------------------------------

def test_evidence_pack_is_bound_to_exact_revisions_and_deterministic():
    first = provider().lookup(request())
    again = provider().lookup(request())
    other_revision = provider().lookup(request(binding=BINDING | {"project_revision": 4}))
    other_land = provider().lookup(request(binding=BINDING | {"land_revision_id": "44444444-4444-4444-8444-444444444444"}))

    assert first["binding"] == BINDING and first["table_version"] == "synthetic-0.1"
    assert first["evidence_pack_id"] == again["evidence_pack_id"]
    assert len({first["evidence_pack_id"], other_revision["evidence_pack_id"], other_land["evidence_pack_id"]}) == 3


def test_shared_contract_example_is_the_real_provider_output():
    actual = provider().lookup(request(kinds=["REAR_SPACE_MIN", "SIDE_SPACE_MIN", "FLOOR_AREA_RATIO_MAX"]))

    assert json.loads(EXAMPLE.read_text(encoding="utf-8")) == actual


# ---- table loading: refuse anything unreviewable --------------------------------------------------------------------

def _mutated(constraint_id, **changes):
    table = copy.deepcopy(TABLE)
    for c in table["constraints"]:
        if c["constraint_id"] == constraint_id:
            c.update(changes)
    return table


@pytest.mark.parametrize("table", [
    _mutated("syn.north.rear-space", unit="percent"),                       # unit/kind mismatch
    _mutated("syn.north.rear-space", comparator="<="),                      # comparator/kind mismatch
    _mutated("syn.north.rear-space", value=-1),                             # out of range
    _mutated("syn.north.rear-space", value=float("nan")),                   # non-finite
    _mutated("syn.north.rear-space", reviewer_role=None),                   # reviewed without reviewer
    _mutated("syn.north.rear-space", unexpected="x"),                       # unknown field
    _mutated("syn.north.rear-space", kind="SETBACK_ANYTHING"),              # unknown kind
    _mutated("syn.north.rear-space", review_status="AUTO_VERIFIED"),        # no automatic verification state exists
    dict(copy.deepcopy(TABLE), contract_version="constraints-1"),           # unknown contract version
])
def test_invalid_tables_are_refused(table):
    with pytest.raises((ValueError, ValidationError)):
        provider(table)


def test_duplicate_constraint_ids_are_refused():
    table = copy.deepcopy(TABLE)
    table["constraints"].append(copy.deepcopy(table["constraints"][0]))

    with pytest.raises(ValueError, match="duplicate"):
        provider(table)


@pytest.mark.parametrize("change", [
    {"excerpt": "A minimum rear space of FIVE metres shall be provided"},  # text not in the source
    {"start_char": 0},                                                      # offsets do not match the excerpt
    {"page": 9},                                                            # page does not exist
    {"document_id": "c" * 64},                                              # document not in the corpus
])
def test_constraints_whose_citation_does_not_reconstruct_are_refused(change):
    table = copy.deepcopy(TABLE)
    table["constraints"][0]["source"] = table["constraints"][0]["source"] | change

    with pytest.raises(ValueError, match="does not reconstruct"):
        provider(table)


# ---- HTTP endpoint ------------------------------------------------------------------------------------------------

def test_endpoint_returns_the_bound_evidence_pack():
    response = client(provider()).post(ENDPOINT, json={"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING})

    assert response.status_code == 200
    assert response.json()["kinds"][0]["status"] == "VERIFIED"


def test_endpoint_without_a_table_is_truthfully_unavailable():
    response = client(None).post(ENDPOINT, json={"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING})

    assert response.status_code == 503 and response.json() == {"code": "CONSTRAINTS_UNAVAILABLE"}


@pytest.mark.parametrize("body", [
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"]},                                   # binding missing
    {"jurisdiction": NORTH_JURISDICTION, "kinds": [], "binding": BINDING},                               # no kinds
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN", "REAR_SPACE_MIN"], "binding": BINDING},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["UNKNOWN_KIND"], "binding": BINDING},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING | {"project_revision": 0}},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING | {"project_revision": "3"}},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING | {"land_revision_id": "not-a-uuid"}},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING, "verified": True},
    {"jurisdiction": NORTH_JURISDICTION, "kinds": ["REAR_SPACE_MIN"], "binding": BINDING, "building_use": "industrial"},
])
def test_endpoint_rejects_invalid_contract_structures(body):
    response = client(provider()).post(ENDPOINT, json=body)

    assert response.status_code == 422 and response.json()["code"] == "VALIDATION_FAILED"


def test_retrieval_never_creates_constraints():
    """A search for a phrase that exists in the corpus does not change the constraint pack."""
    c = client(provider())
    before = c.post(ENDPOINT, json={"jurisdiction": NORTH_JURISDICTION, "kinds": ["SIDE_SPACE_MIN", "FRONT_SETBACK_MIN"], "binding": BINDING}).json()
    c.post("/internal/v1/regulations/search", json={"query": "front setback side space", "jurisdiction": NORTH_JURISDICTION})
    after = c.post(ENDPOINT, json={"jurisdiction": NORTH_JURISDICTION, "kinds": ["SIDE_SPACE_MIN", "FRONT_SETBACK_MIN"], "binding": BINDING}).json()

    assert before == after
    assert by_kind(after)["SIDE_SPACE_MIN"]["status"] == "UNVERIFIED" and by_kind(after)["FRONT_SETBACK_MIN"]["status"] == "MISSING"
