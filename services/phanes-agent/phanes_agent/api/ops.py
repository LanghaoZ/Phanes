from fastapi import APIRouter, Request
from sqlalchemy import text

router = APIRouter(tags=["ops"])


@router.get("/healthz")
async def healthz(request: Request):
    checks: dict[str, object] = {}
    ok = True

    try:
        async with request.app.state.sessionmaker() as db:
            await db.execute(text("SELECT 1"))
        checks["database"] = "ok"
    except Exception as exc:
        checks["database"] = f"error: {type(exc).__name__}"
        ok = False

    registry = request.app.state.registry
    checks["agent_types"] = sorted(registry.types.keys())
    if registry.rejected:
        checks["rejected_agent_types"] = dict(registry.rejected)

    checks["phoenix_enabled"] = request.app.state.settings.phoenix_enabled

    return {"status": "ok" if ok else "degraded", "checks": checks}
