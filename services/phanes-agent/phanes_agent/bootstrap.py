"""Global SDK configuration — runs once at startup, before any Agent is built.

Order matters:
1. OpenRouter as the default client (use_for_tracing=False — the default True
   would send the OpenRouter key to OpenAI's trace-ingest endpoint).
2. Responses API (decided 2026-08-02): OpenRouter serves /api/v1/responses
   (beta) and it's the SDK-native shape — richer reasoning-item handling,
   and it unlocks the sandbox Compaction capability later. Rollback is a
   one-liner: set_default_openai_api("chat_completions") (+ the
   use_responses flags in core/runs.py).
3. Replace the default trace processors (which upload to OpenAI) with our
   local MySQL processor.
4. Attach Phoenix (OpenInference instrumentation) as the second pipeline —
   AFTER set_trace_processors, which would wipe it otherwise.
"""

import logging

from openai import AsyncOpenAI

from .config import Settings
from .core.tracing import LocalTraceProcessor

logger = logging.getLogger(__name__)


def configure_models(settings: Settings) -> None:
    if not settings.openrouter_api_key:
        raise RuntimeError(
            "PHANES_API_KEY (or OPENROUTER_API_KEY) is not set. Export it as "
            "an environment variable (or put it in .env); see .env.example."
        )

    from agents import set_default_openai_api, set_default_openai_client

    client = AsyncOpenAI(
        base_url=settings.openrouter_base_url,
        api_key=settings.openrouter_api_key,
    )
    set_default_openai_client(client, use_for_tracing=False)
    set_default_openai_api("responses")


def configure_tracing(settings: Settings) -> LocalTraceProcessor:
    from agents.tracing import set_trace_processors

    processor = LocalTraceProcessor(settings.database_url_sync)
    set_trace_processors([processor])

    if settings.phoenix_enabled:
        try:
            from openinference.instrumentation.openai_agents import (
                OpenAIAgentsInstrumentor,
            )
            from phoenix.otel import register

            tracer_provider = register(
                project_name=settings.phoenix_project,
                endpoint=f"{settings.phoenix_collector_endpoint}/v1/traces",
                set_global_tracer_provider=False,
            )
            # exclusive_processor=False is load-bearing: the default (True)
            # calls set_trace_processors([openinference]) and silently wipes
            # the MySQL processor; False appends alongside it.
            OpenAIAgentsInstrumentor().instrument(
                tracer_provider=tracer_provider, exclusive_processor=False
            )
            logger.info(
                "Phoenix instrumentation attached. endpoint=%s project=%s",
                settings.phoenix_collector_endpoint,
                settings.phoenix_project,
            )
        except Exception:
            logger.warning(
                "Phoenix instrumentation failed to attach; continuing without it "
                "(MySQL trace pipeline is unaffected).",
                exc_info=True,
            )

    return processor
