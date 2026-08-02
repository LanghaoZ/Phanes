"""AgentSession resolution.

Slice 1 rules (design: phanes_agent_design.md, "AgentSession resolution"):
- plain run            → new one-shot AgentSession (history off)
- session_id given     → join that AgentSession (404 if absent)
- session_key given    → get-or-create the persistent AgentSession (history on)
"""

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from ulid import ULID

from ..storage.models import AgentSessionRow, utcnow


class SessionNotFoundError(Exception):
    pass


async def resolve_session(
    db: AsyncSession,
    *,
    session_id: str | None = None,
    session_key: str | None = None,
) -> AgentSessionRow:
    if session_id is not None:
        row = await db.get(AgentSessionRow, session_id)
        if row is None:
            raise SessionNotFoundError(f"AgentSession '{session_id}' not found")
        row.last_activity_at = utcnow()
        return row

    if session_key is not None:
        result = await db.execute(
            select(AgentSessionRow).where(AgentSessionRow.session_key == session_key)
        )
        row = result.scalar_one_or_none()
        if row is not None:
            row.last_activity_at = utcnow()
            return row
        row = AgentSessionRow(
            session_id=str(ULID()),
            session_key=session_key,
            origin="api",
            history=True,
        )
        db.add(row)
        return row

    row = AgentSessionRow(session_id=str(ULID()), origin="api", history=False)
    db.add(row)
    return row
