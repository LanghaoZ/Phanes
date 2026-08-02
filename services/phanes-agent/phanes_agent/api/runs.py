from datetime import datetime

from fastapi import APIRouter, HTTPException, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from ..core.registry import UnknownAgentTypeError
from ..core.runs import RunService
from ..core.sessions import SessionNotFoundError
from ..storage.models import RunRow

router = APIRouter(tags=["runs"])


class CreateRunRequest(BaseModel):
    agent_type: str
    input: str = Field(min_length=1)
    session_id: str | None = None
    session_key: str | None = None
    source: str | None = None


class RunOut(BaseModel):
    run_id: str
    session_id: str
    agent_type: str
    origin: str
    source: str | None
    status: str
    input: str
    final_output: str | None
    error: str | None
    trace_id: str | None
    input_tokens: int
    output_tokens: int
    created_at: datetime
    started_at: datetime | None
    ended_at: datetime | None

    @classmethod
    def from_row(cls, row: RunRow) -> "RunOut":
        return cls(
            run_id=row.run_id,
            session_id=row.session_id,
            agent_type=row.agent_type,
            origin=row.origin,
            source=row.source,
            status=row.status,
            input=row.input,
            final_output=row.final_output,
            error=row.error,
            trace_id=row.trace_id,
            input_tokens=row.input_tokens,
            output_tokens=row.output_tokens,
            created_at=row.created_at,
            started_at=row.started_at,
            ended_at=row.ended_at,
        )


def _service(request: Request) -> RunService:
    return request.app.state.run_service


@router.post("/runs")
async def create_run(
    request: Request, body: CreateRunRequest, wait: bool = Query(default=False)
):
    if body.session_id and body.session_key:
        raise HTTPException(
            status_code=422, detail="Pass either session_id or session_key, not both"
        )
    try:
        run = await _service(request).create_run(
            agent_type=body.agent_type,
            input_text=body.input,
            session_id=body.session_id,
            session_key=body.session_key,
            source=body.source,
            wait=wait,
        )
    except UnknownAgentTypeError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except SessionNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc

    payload = RunOut.from_row(run).model_dump(mode="json")
    return JSONResponse(payload, status_code=200 if wait else 202)


@router.get("/runs")
async def list_runs(
    request: Request,
    agent_type: str | None = None,
    status: str | None = None,
    limit: int = Query(default=50, ge=1, le=500),
) -> list[RunOut]:
    rows = await _service(request).list_runs(
        agent_type=agent_type, status=status, limit=limit
    )
    return [RunOut.from_row(r) for r in rows]


@router.get("/runs/{run_id}")
async def get_run(request: Request, run_id: str) -> RunOut:
    row = await _service(request).get_run(run_id)
    if row is None:
        raise HTTPException(status_code=404, detail=f"Run '{run_id}' not found")
    return RunOut.from_row(row)
