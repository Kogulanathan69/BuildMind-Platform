"""BuildMind private regulation retrieval service (BE-M7, contract v1).

Internal only: bind to loopback or a private network (G11 transport/auth still open). Interactive docs and the
OpenAPI document are disabled. Errors never include exception text, file paths or request input.
"""
import logging
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from .contracts import CONTRACT_VERSION, SearchRequest, SearchResponse, SourcesResponse
from .engine import EngineSettings, RetrievalEngine, Step6Engine
from .mapping import to_search_response, to_sources

SERVICE_NAME = "buildmind-rag"
MAX_BODY_BYTES = 16 * 1024
logger = logging.getLogger(SERVICE_NAME)


def _problem(status: int, code: str, errors: list[dict[str, Any]] | None = None) -> JSONResponse:
    body: dict[str, Any] = {"code": code}
    if errors:
        body["errors"] = errors
    return JSONResponse(status_code=status, content=body)


def create_app(engine: RetrievalEngine | None) -> FastAPI:
    app = FastAPI(title="BuildMind regulation retrieval", docs_url=None, redoc_url=None, openapi_url=None)

    @app.middleware("http")
    async def limit_body(request: Request, call_next):
        length = request.headers.get("content-length")
        if length is not None and (not length.isdigit() or int(length) > MAX_BODY_BYTES):
            return _problem(413, "PAYLOAD_TOO_LARGE")
        return await call_next(request)

    @app.exception_handler(RequestValidationError)
    async def validation_failed(_: Request, exc: RequestValidationError) -> JSONResponse:
        # Location and message only: the offending input is never echoed back.
        errors = [{"loc": [str(part) for part in error.get("loc", ())], "msg": error.get("msg", "")} for error in exc.errors()]
        return _problem(422, "VALIDATION_FAILED", errors)

    @app.exception_handler(Exception)
    async def unexpected(_: Request, exc: Exception) -> JSONResponse:
        logger.error("Unhandled %s in regulation retrieval", type(exc).__name__)
        return _problem(500, "INTERNAL_ERROR")

    @app.get("/health")
    def health() -> dict[str, Any]:
        return {"service": SERVICE_NAME, "status": "ok", "contract_version": CONTRACT_VERSION, "corpus_loaded": engine is not None}

    @app.post("/internal/v1/regulations/search", response_model=SearchResponse)
    def search(request: SearchRequest):
        if engine is None:
            return _problem(503, "CORPUS_UNAVAILABLE")
        filters: dict[str, str] = {}
        if request.jurisdiction is not None:
            filters["jurisdiction"] = request.jurisdiction
        if request.document_ids is not None and len(request.document_ids) == 1:
            filters["document_id"] = request.document_ids[0]
        try:
            result = engine.search(request.query, request.k, filters, request.document_ids)
        except ValueError:
            # Engine-side validation (for example an unsupported filter combination). Message deliberately dropped.
            return _problem(422, "VALIDATION_FAILED")
        return to_search_response(result)

    @app.get("/internal/v1/regulations/sources", response_model=SourcesResponse)
    def sources():
        if engine is None:
            return _problem(503, "CORPUS_UNAVAILABLE")
        return SourcesResponse(sources=to_sources(engine.registry()))

    return app


def _load_engine() -> RetrievalEngine | None:
    settings = EngineSettings.from_environment()
    if settings is None:
        logger.warning("Regulation corpus not configured; search returns 503 CORPUS_UNAVAILABLE")
        return None
    return Step6Engine(settings)  # fails fast on any identity/hash mismatch


app = create_app(_load_engine())
