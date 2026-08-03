"""Seed dev sources for the assistant agent type — idempotent.

- Phoenix: ensure prompt 'assistant' has a version tagged with the current
  env tag carrying the expected text (creates + tags only when missing or
  text differs).
- phanes-config: ensure agent-types/assistant document matches (PUTs a new
  version only on change).

Run: uv run python scripts/seed_dev.py
"""

import asyncio
import sys
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from phanes_agent.config import Settings  # noqa: E402

ASSISTANT_PROMPT = (
    "You are a concise personal assistant on the Phanes platform.\n"
    "Reply directly and briefly. No preamble, no restating the question."
)

ASSISTANT_DOC = {
    "type_name": "assistant",
    "enabled": True,
    "prompt_key": "assistant",
    "model": "deepseek/deepseek-v4-flash",
    "model_settings": {"max_tokens": 1024},
    "tools": [],
    "mcp_servers": [],
    "skills": [],
    "sandbox": False,
    "memory": False,
    "conversational": False,
    "collaborators": [],
    "dispatch_targets": [],
    "continuation_policy": None,
}


async def seed_phoenix(settings: Settings) -> None:
    from phoenix.client import AsyncClient
    from phoenix.client.types.prompts import PromptVersion

    client = AsyncClient(base_url=settings.phoenix_collector_endpoint)
    tag = settings.resolved_prompt_tag

    try:
        existing = await client.prompts.get(prompt_identifier="assistant", tag=tag)
        formatted = existing.format()
        current = "\n\n".join(
            m["content"] for m in formatted.messages if isinstance(m.get("content"), str)
        ).strip()
        if current == ASSISTANT_PROMPT.strip():
            print(f"phoenix: prompt 'assistant' tag '{tag}' already up to date")
            return
        print(f"phoenix: prompt text differs under tag '{tag}', creating new version")
    except Exception:
        print(f"phoenix: no 'assistant' prompt under tag '{tag}', creating")

    version = await client.prompts.create(
        name="assistant",
        prompt_description="Phanes assistant agent type",
        version=PromptVersion(
            [{"role": "system", "content": ASSISTANT_PROMPT}],
            model_name=ASSISTANT_DOC["model"],
            model_provider="DEEPSEEK",
        ),
    )
    await client.prompts.tags.create(prompt_version_id=version.id, name=tag)
    print(f"phoenix: created prompt version {version.id}, tagged '{tag}'")


async def seed_config(settings: Settings) -> None:
    async with httpx.AsyncClient(base_url=settings.config_service_url) as http:
        url = f"/configs/{settings.config_namespace}/agent-types/assistant"
        resp = await http.get(url)
        if resp.status_code == 200 and resp.json()["doc"] == ASSISTANT_DOC:
            print(f"phanes-config: assistant doc v{resp.json()['version']} up to date")
            return
        resp = await http.put(url, json={"doc": ASSISTANT_DOC, "author": "seed_dev"})
        resp.raise_for_status()
        print(f"phanes-config: wrote assistant doc v{resp.json()['version']}")


async def main() -> None:
    settings = Settings()
    await seed_phoenix(settings)
    await seed_config(settings)


if __name__ == "__main__":
    asyncio.run(main())
