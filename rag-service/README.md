# RAG service — M7 ownership

This directory is deliberately **documentation-only**. It must not contain legal/reference PDFs, personal data, raw surveys, OCR caches, extracted text, indexes, embeddings, evaluation datasets or API keys — this repository is public.

> Reconciled 2026-11-17 (BE-M7-D01). Earlier text pointed to `docs/second-brain/13-Implementation-Blueprint/` (now historical V2) and described embeddings as part of the service; embeddings are **not implemented**.

## Where the engine is

The existing retrieval engine is **not in this repository and not under version control**. Candidate canonical implementation (pending lead confirmation, G10): `C:\BuildMind-PDF-Processor\regulation_retrieval` on the lead's workstation. Older staging/backup copies exist elsewhere and are not canonical. Do not copy the extraction workspace (≈12 GB, includes source PDFs) into this repo. Proposed version-control and integration approach: [16-RAG-SOURCE-STRATEGY-PROPOSAL](../docs/second-brain/14-Current-Backend/16-RAG-SOURCE-STRATEGY-PROPOSAL.md) (proposal, not approved).

## Verified vs planned (as of 2026-11-17, static inspection only)

| Item | State |
| --- | --- |
| PDF extraction corpus | Exists as pipeline output (746 master records, 543 unique PDFs, 44,855 processed pages per its own summary). Not independently re-verified. Not the retrieval corpus. |
| Indexed retrieval corpus | **1 document** (UDA Gazette 2235/54). Legal currency unverified. |
| Retrieval method | **BM25 lexical ranking, Python standard library only**. Python API `RegulationRetrievalService(index_path).search(query, k, filters)` returning page-located passages. |
| HTTP route | **None.** No FastAPI/internal service exists for retrieval. |
| Embeddings / vector store | **Planned only.** A separate embedding smoke script exists; no approved model, store or integration. |
| Tests | Historical: 93 unit tests reported OK in a log dated 2026-11-13. **Not re-run** in the 2026-11-17 audit. |
| Generated answers / LLM | None. |

Details and evidence: [08-RAG](../docs/second-brain/14-Current-Backend/08-RAG.md), [audit](../docs/second-brain/17-Progress/07-AUDIT-2026-11-17.md).

## Responsibilities

M7: source provenance, corpus manifest, document metadata, chunking, retrieval, citations, jurisdiction/applicability status, evaluation evidence. M4: shared C# service-client infrastructure (timeouts, auth, correlation, safe errors). M6: consumes source-linked evidence; deterministic validation remains authoritative. Relevance scores are not legal confidence; retrieved text is untrusted data, never instructions.
