import asyncio

import pytest
from fastapi.testclient import TestClient

from phanes_agent.clients.prompts import PromptResolutionError, ResolvedPrompt
from phanes_agent.config import Settings
from phanes_agent.core.registry import Registry, build_registry
from phanes_agent.main import create_app

ASSISTANT_DOC = {
    "type_name": "assistant",
    "prompt_key": "assistant",
    "model": "deepseek/deepseek-v4-flash",
    "model_settings": {"max_tokens": 256},
}


class FakePromptResolver:
    def __init__(self, prompts: dict[str, str]):
        self._prompts = prompts
        self.invalidations = 0

    async def resolve(self, prompt_key: str) -> ResolvedPrompt:
        if prompt_key not in self._prompts:
            raise PromptResolutionError(f"prompt '{prompt_key}' unavailable")
        return ResolvedPrompt(
            text=self._prompts[prompt_key], version_id=f"pv-{prompt_key}"
        )

    def invalidate(self) -> None:
        self.invalidations += 1


class StaticRegistryManager:
    def __init__(self, registry: Registry):
        self.current = registry
        self.refresh_calls = 0

    async def refresh(self, force: bool = False) -> bool:
        self.refresh_calls += 1
        return False


def make_registry(
    docs: dict[str, tuple[int, dict]] | None = None,
    prompts: dict[str, str] | None = None,
) -> Registry:
    docs = docs if docs is not None else {"agent-types/assistant": (1, ASSISTANT_DOC)}
    prompts = prompts if prompts is not None else {"assistant": "You are a test assistant."}
    return asyncio.run(build_registry(docs, FakePromptResolver(prompts)))


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(
        openrouter_api_key="test-key-not-real",
        database_url=f"sqlite+aiosqlite:///{tmp_path}/test.db",
        phoenix_enabled=False,
        create_tables=True,
        run_timeout_seconds=10,
        log_dir=tmp_path / "logs",
        _env_file=None,
    )


@pytest.fixture
def client(settings):
    app = create_app(settings, registry_manager=StaticRegistryManager(make_registry()))
    with TestClient(app) as c:
        yield c
