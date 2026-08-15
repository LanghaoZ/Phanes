import pytest
from tests.conftest import ASSISTANT_DOC, FakePromptResolver

from phanes_agent.core.registry import (
    RegistryManager,
    UnknownAgentTypeError,
    build_registry,
)


async def test_builds_valid_type():
    registry = await build_registry(
        {"agent-types/assistant": (3, ASSISTANT_DOC)},
        FakePromptResolver({"assistant": "prompt text"}),
    )
    entry = registry.get("assistant")
    assert entry.agent.name == "assistant"
    assert entry.agent.instructions == "prompt text"
    assert entry.config_version == 3
    assert entry.prompt_version_id == "pv-assistant"
    assert not registry.rejected


async def test_rejects_unsupported_capabilities():
    doc = {**ASSISTANT_DOC, "type_name": "sandboxed", "sandbox": True}
    registry = await build_registry(
        {"agent-types/sandboxed": (1, doc)},
        FakePromptResolver({"assistant": "x"}),
    )
    assert "sandboxed" in registry.rejected
    with pytest.raises(UnknownAgentTypeError, match="invalid"):
        registry.get("sandboxed")


async def test_rejects_key_name_mismatch():
    registry = await build_registry(
        {"agent-types/other": (1, ASSISTANT_DOC)},
        FakePromptResolver({"assistant": "x"}),
    )
    assert "does not match key" in registry.rejected["other"]


async def test_rejects_unresolvable_prompt():
    registry = await build_registry(
        {"agent-types/assistant": (1, ASSISTANT_DOC)},
        FakePromptResolver({}),
    )
    assert "prompt resolution failed" in registry.rejected["assistant"]


async def test_unknown_type_raises():
    registry = await build_registry({}, FakePromptResolver({}))
    with pytest.raises(UnknownAgentTypeError, match="Unknown"):
        registry.get("ghost")


class FakeConfigClient:
    def __init__(self):
        self.versions: dict[str, int] = {"agent-types/assistant": 1}
        self.docs: dict[str, dict] = {"agent-types/assistant": ASSISTANT_DOC}
        self.down = False
        self.list_calls = 0

    async def list_agent_type_versions(self):
        self.list_calls += 1
        if self.down:
            raise ConnectionError("config service down")
        return dict(self.versions)

    async def get_doc(self, key):
        if self.down:
            raise ConnectionError("config service down")
        return self.versions[key], self.docs[key]


async def test_manager_refresh_and_version_caching():
    config = FakeConfigClient()
    resolver = FakePromptResolver({"assistant": "v1 prompt"})
    manager = RegistryManager(config, resolver)

    assert await manager.refresh() is True
    assert "assistant" in manager.current.types
    # Same versions → no rebuild.
    assert await manager.refresh() is False
    # Version bump → rebuild.
    config.versions["agent-types/assistant"] = 2
    assert await manager.refresh() is True
    assert manager.current.types["assistant"].config_version == 2


async def test_manager_keeps_last_good_when_config_down():
    config = FakeConfigClient()
    resolver = FakePromptResolver({"assistant": "prompt"})
    manager = RegistryManager(config, resolver)
    await manager.refresh()
    assert "assistant" in manager.current.types

    config.down = True
    assert await manager.refresh() is False
    assert "assistant" in manager.current.types  # last-good survives


async def test_manager_force_invalidates_prompt_cache():
    config = FakeConfigClient()
    resolver = FakePromptResolver({"assistant": "prompt"})
    manager = RegistryManager(config, resolver)
    await manager.refresh()
    assert resolver.invalidations == 0
    await manager.refresh(force=True)
    assert resolver.invalidations == 1
