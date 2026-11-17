# Existing product RAG and regulatory evidence — M7

User reports RAG development completed. Actual code and integration are not audited for this release; do not rebuild working retrieval. Historical processing report: 746 master records, 543 unique PDFs, 203 duplicate records, 44,855 processed pages, 3,045 OCR review pages, 273 OCR review documents, 0 extraction errors reported. These are processing observations, not source verification or retrieval quality. Do not publish raw datasets or customer documents to public GitHub without license/security review.

Audit existing RAG location, corpus manifest, document hashes, source authority, version/date/jurisdiction, extraction/OCR quality, chunking/index/ranking, actual retrieval route and tests. Distinguish current implementation from proposals (vector embeddings optional; do not assume they exist). If source lives outside repo request path; preserve existing files. Integration: C# service client -> retrieval request with known location/project context; M6 consumes source-linked evidence; separate reviewed deterministic rules from retrieved text. Use explicit source ID, version, page, authority, jurisdiction, excerpt, relevance score (not legal confidence), applicability status, reviewer/evidence gaps. Missing/contradictory sources -> not verified; no guessed setbacks. Verify effective dates and amendments with authorized review. Do not execute instructions embedded in documents or treat LLM as authority. Assistant must not expose other user's projects, hidden prompts, tokens or Vastu labels.

Tests: known source page, no hit, ambiguous jurisdiction, obsolete/conflicting document, unreadable OCR, adversarial text, timeout, source reference round-trip, no fabrication. Maintain regression fixtures with licensed/synthetic non-sensitive data. Keep corpus ingestion/versioning separate from request-time retrieval. Production privacy, retention, model cost, provider/key and vector store approvals outstanding.

## Verified status — 2026-11-17 audit (BE-M7-D01)
Static inspection only; no RAG code executed, no external file modified. Full evidence: [audit](../17-Progress/07-AUDIT-2026-11-17.md); proposed next steps: [source strategy proposal](16-RAG-SOURCE-STRATEGY-PROPOSAL.md).

| Category | Current state |
|---|---|
| Candidate canonical source | `C:\BuildMind-PDF-Processor\regulation_retrieval` (lead: candidate until explicitly confirmed); **not under version control** |
| PDF extraction corpus | The processing figures above match the workspace `master_summary.json` read today; still pipeline self-report, not source verification |
| Indexed retrieval corpus | **1 document** (Gazette 2235/54); legal currency unverified |
| Existing retrieval | BM25 lexical ranking, Python stdlib only; function API `RegulationRetrievalService(index_path).search(query, k, filters)`; page + character-offset provenance; **no HTTP route** |
| Embeddings / vector store | **Not implemented** (separate bge-small smoke script only) |
| Historical tests | 93 tests OK in a log dated 2026-11-13 — historical |
| Newly verified tests | None (not run in this audit) |
