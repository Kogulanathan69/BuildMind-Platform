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

Until resolution, mark relevant work BLOCKED or PROPOSED; test fixtures must be explicitly labeled. No unsupported compliance, delivery readiness, or live integration claims.
