"""Run execution service.

Runs are in-process, best-effort asyncio tasks (durability belongs to
Phanes-Task). Two concurrency controls from the design:
- serialization invariant: at most ONE active Run per (AgentSession,
  AgentType) — per-pair asyncio.Lock (FIFO waiters);
- a global semaphore caps total concurrent runs.
"""

import asyncio
import logging

from agents import Runner
from agents.models.multi_provider import MultiProvider
from agents.run_config import RunConfig
from agents.tracing import gen_trace_id
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker
from ulid import ULID

from ..config import Settings
from ..storage.models import AgentSessionRow, RunRow, utcnow
from .registry import Registry
from .sessions import resolve_session

logger = logging.getLogger(__name__)


def _flush_traces() -> None:
    try:
        from agents.tracing import flush_traces

        flush_traces()
    except Exception:  # tracing must never break a run
        logger.debug("flush_traces unavailable or failed", exc_info=True)


class RunService:
    def __init__(
        self,
        registry: Registry,
        sessionmaker: async_sessionmaker[AsyncSession],
        settings: Settings,
    ):
        self._registry = registry
        self._sessionmaker = sessionmaker
        self._settings = settings
        self._semaphore = asyncio.Semaphore(settings.max_concurrent_runs)
        self._pair_locks: dict[tuple[str, str], asyncio.Lock] = {}
        self._tasks: dict[str, asyncio.Task] = {}
        # OpenRouter model IDs are namespaced ("deepseek/...", "openai/...").
        # The SDK's default MultiProvider treats the namespace as a routing
        # prefix ("Unknown prefix: deepseek") or strips "openai/". model_id
        # modes pass the full string through to the (OpenRouter) client,
        # which serves them on its Responses endpoint (/api/v1/responses).
        self._model_provider = MultiProvider(
            unknown_prefix_mode="model_id",
            openai_prefix_mode="model_id",
            openai_use_responses=True,
        )

    def _pair_lock(self, session_id: str, agent_type: str) -> asyncio.Lock:
        return self._pair_locks.setdefault((session_id, agent_type), asyncio.Lock())

    async def create_run(
        self,
        *,
        agent_type: str,
        input_text: str,
        session_id: str | None = None,
        session_key: str | None = None,
        source: str | None = None,
        wait: bool = False,
    ) -> RunRow:
        self._registry.get(agent_type)  # validate up front; raises UnknownAgentTypeError

        async with self._sessionmaker() as db:
            session = await resolve_session(
                db, session_id=session_id, session_key=session_key
            )
            run = RunRow(
                run_id=str(ULID()),
                session_id=session.session_id,
                agent_type=agent_type,
                origin="api",
                source=source,
                input=input_text,
                status="queued",
                trace_id=gen_trace_id(),
            )
            db.add(run)
            await db.commit()
            run_id, resolved_session_id, trace_id = (
                run.run_id,
                run.session_id,
                run.trace_id,
            )

        task = asyncio.create_task(
            self._execute(
                run_id=run_id,
                session_id=resolved_session_id,
                trace_id=trace_id,
                agent_type=agent_type,
                input_text=input_text,
                source=source,
            ),
            name=f"run-{run_id}",
        )
        self._tasks[run_id] = task
        task.add_done_callback(lambda _t: self._tasks.pop(run_id, None))

        if wait:
            await asyncio.shield(task)

        return await self.get_run(run_id)  # type: ignore[return-value]

    async def _execute(
        self,
        *,
        run_id: str,
        session_id: str,
        trace_id: str | None,
        agent_type: str,
        input_text: str,
        source: str | None,
    ) -> None:
        try:
            async with self._pair_lock(session_id, agent_type):
                async with self._semaphore:
                    await self._mark(run_id, status="running", started=True)
                    try:
                        registered = self._registry.get(agent_type)
                        run_config = RunConfig(
                            model_provider=self._model_provider,
                            workflow_name=agent_type,
                            trace_id=trace_id,
                            group_id=session_id,
                            trace_metadata={
                                "run_id": run_id,
                                "origin": "api",
                                "source": source or "",
                            },
                        )
                        result = await asyncio.wait_for(
                            Runner.run(
                                registered.agent, input_text, run_config=run_config
                            ),
                            timeout=self._settings.run_timeout_seconds,
                        )
                        usage = getattr(result.context_wrapper, "usage", None)
                        await self._finalize(
                            run_id,
                            status="succeeded",
                            final_output=str(result.final_output),
                            input_tokens=getattr(usage, "input_tokens", 0) or 0,
                            output_tokens=getattr(usage, "output_tokens", 0) or 0,
                        )
                    except asyncio.TimeoutError:
                        await self._finalize(
                            run_id,
                            status="failed",
                            error=(
                                "Run timed out after "
                                f"{self._settings.run_timeout_seconds}s"
                            ),
                        )
                    except Exception as exc:
                        logger.exception("Run %s failed", run_id)
                        await self._finalize(
                            run_id, status="failed", error=f"{type(exc).__name__}: {exc}"
                        )
        finally:
            _flush_traces()

    async def _mark(self, run_id: str, *, status: str, started: bool = False) -> None:
        async with self._sessionmaker() as db:
            run = await db.get(RunRow, run_id)
            if run is None:
                return
            run.status = status
            if started:
                run.started_at = utcnow()
            await db.commit()

    async def _finalize(
        self,
        run_id: str,
        *,
        status: str,
        final_output: str | None = None,
        error: str | None = None,
        input_tokens: int = 0,
        output_tokens: int = 0,
    ) -> None:
        async with self._sessionmaker() as db:
            run = await db.get(RunRow, run_id)
            if run is None:
                return
            run.status = status
            run.final_output = final_output
            run.error = error
            run.input_tokens = input_tokens
            run.output_tokens = output_tokens
            run.ended_at = utcnow()
            session = await db.get(AgentSessionRow, run.session_id)
            if session is not None:
                session.last_activity_at = utcnow()
            await db.commit()

    async def get_run(self, run_id: str) -> RunRow | None:
        async with self._sessionmaker() as db:
            return await db.get(RunRow, run_id)

    async def list_runs(
        self,
        *,
        agent_type: str | None = None,
        status: str | None = None,
        limit: int = 50,
    ) -> list[RunRow]:
        stmt = select(RunRow).order_by(RunRow.created_at.desc()).limit(limit)
        if agent_type:
            stmt = stmt.where(RunRow.agent_type == agent_type)
        if status:
            stmt = stmt.where(RunRow.status == status)
        async with self._sessionmaker() as db:
            result = await db.execute(stmt)
            return list(result.scalars())
