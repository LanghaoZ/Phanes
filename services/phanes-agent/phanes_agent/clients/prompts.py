"""Phoenix Prompts resolver.

Prompts are versioned entities in the self-hosted Phoenix; we pull by
(name, tag) — dev pulls `development`, prod pulls `production`. Moving a
tag IS the prompt deployment. Resilience: TTL cache + last-good — a
Phoenix outage serves the cached text and never stops agents. The
resolved prompt version id is recorded per run for reproducibility.
"""

import logging
import time
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class ResolvedPrompt:
    text: str
    version_id: str


class PromptResolutionError(Exception):
    pass


def _extract_text(prompt_version) -> str:
    """Pull the instruction text out of a Phoenix chat-template prompt."""
    formatted = prompt_version.format()
    messages = getattr(formatted, "messages", None) or []
    parts: list[str] = []
    for message in messages:
        content = message.get("content") if isinstance(message, dict) else None
        if isinstance(content, str):
            parts.append(content)
        elif isinstance(content, list):  # content-part form
            parts.extend(
                p.get("text", "") for p in content if isinstance(p, dict)
            )
    text = "\n\n".join(p for p in parts if p).strip()
    if not text:
        raise PromptResolutionError("prompt template resolved to empty text")
    return text


class PhoenixPromptResolver:
    def __init__(self, base_url: str, tag: str, cache_ttl_seconds: float = 300.0):
        from phoenix.client import AsyncClient

        self._client = AsyncClient(base_url=base_url)
        self._tag = tag
        self._ttl = cache_ttl_seconds
        self._cache: dict[str, tuple[float, ResolvedPrompt]] = {}

    @property
    def tag(self) -> str:
        return self._tag

    def invalidate(self) -> None:
        self._cache.clear()

    async def resolve(self, prompt_key: str) -> ResolvedPrompt:
        cached = self._cache.get(prompt_key)
        if cached is not None and (time.monotonic() - cached[0]) < self._ttl:
            return cached[1]

        try:
            version = await self._client.prompts.get(
                prompt_identifier=prompt_key, tag=self._tag
            )
            resolved = ResolvedPrompt(text=_extract_text(version), version_id=version.id)
        except Exception as exc:
            if cached is not None:  # serve stale over failing
                logger.warning(
                    "Phoenix prompt fetch failed for '%s' (%s); serving last-good",
                    prompt_key,
                    type(exc).__name__,
                )
                return cached[1]
            raise PromptResolutionError(
                f"prompt '{prompt_key}' (tag '{self._tag}') unavailable: {exc}"
            ) from exc

        self._cache[prompt_key] = (time.monotonic(), resolved)
        return resolved
