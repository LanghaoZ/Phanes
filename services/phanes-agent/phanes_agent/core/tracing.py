"""Local trace pipeline: SDK TracingProcessor → MySQL (system-of-record).

The SDK invokes processor callbacks on hot paths, so this processor only
enqueues; a daemon thread with its own sync SQLAlchemy engine drains the
queue in batches. Errors are swallowed (logged) — tracing must never break
a run. Phoenix is the second, independent pipeline (OpenInference
instrumentation), attached in bootstrap.
"""

import json
import logging
import queue
import threading
from typing import Any

from agents.tracing import Span, Trace
from agents.tracing.processor_interface import TracingProcessor
from sqlalchemy import create_engine
from sqlalchemy.orm import Session as SqlSession

from ..storage.models import SpanRow, TraceRow

logger = logging.getLogger(__name__)

_SENTINEL = object()


def _jsonable(value: Any) -> Any:
    try:
        json.dumps(value)
        return value
    except (TypeError, ValueError):
        return json.loads(json.dumps(value, default=str))


class LocalTraceProcessor(TracingProcessor):
    def __init__(self, sync_database_url: str, batch_size: int = 50):
        self._url = sync_database_url
        self._batch_size = batch_size
        self._queue: queue.Queue[Any] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._started = threading.Event()

    # -- TracingProcessor interface (never raise, never block) ---------------

    def on_trace_start(self, trace: Trace) -> None:
        self._enqueue(("trace_start", trace.export()))

    def on_trace_end(self, trace: Trace) -> None:
        self._enqueue(("trace_end", trace.export()))

    def on_span_start(self, span: Span[Any]) -> None:
        # Spans are persisted once complete (on_span_end).
        pass

    def on_span_end(self, span: Span[Any]) -> None:
        self._enqueue(("span_end", span.export()))

    def shutdown(self, timeout: float | None = None) -> None:
        if self._thread is not None:
            self._queue.put(_SENTINEL)
            self._thread.join(timeout=timeout or 5.0)

    def force_flush(self) -> None:
        self._queue.join()

    # -- internals -----------------------------------------------------------

    def _enqueue(self, item: tuple[str, dict | None]) -> None:
        try:
            self._ensure_thread()
            self._queue.put(item)
        except Exception:
            logger.exception("Failed to enqueue trace item")

    def _ensure_thread(self) -> None:
        if not self._started.is_set():
            self._started.set()
            self._thread = threading.Thread(
                target=self._run, name="phanes-trace-writer", daemon=True
            )
            self._thread.start()

    def _run(self) -> None:
        engine = create_engine(self._url, pool_pre_ping=True)
        while True:
            item = self._queue.get()
            if item is _SENTINEL:
                self._queue.task_done()
                break
            batch = [item]
            try:
                while len(batch) < self._batch_size:
                    nxt = self._queue.get_nowait()
                    if nxt is _SENTINEL:
                        self._queue.put(_SENTINEL)
                        break
                    batch.append(nxt)
            except queue.Empty:
                pass

            try:
                with SqlSession(engine) as db:
                    for kind, payload in batch:
                        if payload is not None:
                            self._apply(db, kind, payload)
                    db.commit()
            except Exception:
                logger.exception("Trace writer batch failed (dropped %d)", len(batch))
            finally:
                for _ in batch:
                    self._queue.task_done()
        engine.dispose()

    def _apply(self, db: SqlSession, kind: str, payload: dict) -> None:
        if kind in ("trace_start", "trace_end"):
            db.merge(
                TraceRow(
                    trace_id=payload.get("id"),
                    workflow_name=payload.get("workflow_name"),
                    group_id=payload.get("group_id"),
                    trace_metadata=_jsonable(payload.get("metadata")),
                )
            )
        elif kind == "span_end":
            span_data = payload.get("span_data") or {}
            db.merge(
                SpanRow(
                    span_id=payload.get("id"),
                    trace_id=payload.get("trace_id"),
                    parent_id=payload.get("parent_id"),
                    span_type=span_data.get("type"),
                    payload=_jsonable(payload),
                    error=_jsonable(payload.get("error")),
                    started_at=payload.get("started_at"),
                    ended_at=payload.get("ended_at"),
                )
            )
