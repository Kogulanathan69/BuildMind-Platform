# API contract register — proposed V3 routes

Public C# base prefix `/api/v1` proposed, align with actual existing routes before freezing. Routes below omit prefix. Python internal routes are never public frontend endpoints. Stable authenticated principal from token, not client user ID. M4 owns global error/auth convention; feature owners own DTOs and endpoint behavior; M7 coordinates register.

| Owner | Method and path | Contract |
|---|---|---|
| M1 | POST /auth/register; POST /auth/login | Input credentials; safe user/session result |
| M1 | POST /auth/forgot-password; POST /auth/reset-password; POST /auth/logout | Generic recovery acknowledgement; one-time token; session policy |
| M1 | GET/PUT /users/me | Allowlisted self profile |
| M1 | POST/GET /projects; GET/PATCH /projects/{id} | Owner-scoped create/list/read/update |
| M2 | POST /projects/{id}/surveys | Multipart private upload -> survey ID/state |
| M2 | GET /projects/{id}/surveys/{surveyId} | Extraction/status with source and uncertainty |
| M2 | GET/PUT /projects/{id}/land | Location/draft geometry, revision |
| M2 | POST /projects/{id}/land/validate; POST /projects/{id}/land/confirm | Candidate checks; exact validated boundary revision |
| M5 | GET /dashboard/summary | Owner-scoped counts/status |
| M5 | GET/PUT /projects/{id}/requirements | Current requirements revision |
| M5 | GET/PUT /projects/{id}/requirements/spaces; GET/PUT /projects/{id}/requirements/relationships | Optional fields, validated references |
| M5 | GET /projects/{id}/requirements/summary; PUT /projects/{id}/design-method | Persisted snapshot and readiness |
| M6 | POST /projects/{id}/generation-jobs | Start generation exact input revisions, idempotency |
| M6 | GET /projects/{id}/generation-jobs/{jobId}; GET same + /alternatives | Real state and comparison |
| M6 | POST /projects/{id}/generation-jobs/{jobId}/select | Exact current alternative reference |
| M3 | POST /projects/{id}/plans; GET same + /{planId} | Create draft and retrieve |
| M3 | POST /projects/{id}/plans/{planId}/edits/validate | Candidate, diagnostics, no persistence |
| M3 | POST /projects/{id}/plans/{planId}/ai-edit/proposals | Propose only, base revision |
| M3 | POST/GET /projects/{id}/plans/{planId}/versions; GET same + /{versionId} | Atomic save; list/retrieve immutable versions |
| M3 | POST /projects/{id}/plans/{planId}/confirm | Explicit acknowledge exact validated version |
| M5 | POST /projects/{id}/reports; GET same + /{reportId}/download | Snapshot/report artifact owner check |
| M5 | POST /projects/{id}/architect-requests; GET same + /{requestId}; POST same + /{requestId}/withdraw | Consent, truthful status, permitted transition |
| M4 | GET /health; GET /health/ready | Process liveness versus dependency readiness |
| M7 | POST /regulations/retrieve (candidate); assistant route TBD | Source-linked evidence; no compliance guarantee |
| TBD | Admin/staff/proposal routes | Approve module, permissions and contracts first |

## Internal Python proposal
- M2: `POST /survey/extract`; `POST /geometry/land/validate`.
- M3: `POST /plans/editor/apply` (pure edit transform; candidate only).
- M6: `POST /geometry/footprint/feasibility`; `POST /plans/generate`; `POST /plans/validate`.
- M7: RAG retrieval route discovered from actual engine; do not invent existing URL.

## Contract conventions (approval required)
JSON camelCase public; explicit Pydantic aliases if internal style differs. 400 malformed; 401 unauthenticated; 403 or consistent 404 non-owner; 409 stale version/conflict; 413 upload too large; 422 domain validation where agreed; 429 throttling; 503 missing dependency; 504 timeout. Problem Details with trace ID and safe errors; no stack traces. Async job 202 with status route; state names frozen in contract. Date/time ISO UTC. Pagination limits. Mutation uses expected revision where required. Never auto-retry non-idempotent mutations. Version breaking changes require ADR, consumer review, migration and tests. Examples are design sketches only. Refer to each member's detailed plan for payload intent.
