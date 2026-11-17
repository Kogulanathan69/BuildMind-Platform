# Survey module

**Owner M2** (survey, land, location, geometry normalization) per [V3 ownership](../../../docs/second-brain/14-Current-Backend/10-OWNERSHIP.md). Reconciled 2026-11-17 (BE-M7-D01); this file previously named M6, which is superseded. M6 is the proposed custodian of `app/main.py` and shared Python dependencies only; register routers through that custodian.

Interface and implementation pending. Proposed internal route (not frozen): `POST /survey/extract` — see [05-API-CONTRACTS](../../../docs/second-brain/14-Current-Backend/05-API-CONTRACTS.md). Internal routes are never public frontend endpoints. Supported formats, OCR, limits and storage remain open (G05). Use synthetic fixtures only; never commit real survey documents.
