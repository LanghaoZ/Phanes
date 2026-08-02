import pytest

from phanes_agent.config import Settings
from phanes_agent.core.registry import UnknownAgentTypeError, load_registry


def _settings(tmp_path, agent_types_yaml: str, prompts_yaml: str) -> Settings:
    types_file = tmp_path / "agent_types.yaml"
    prompts_file = tmp_path / "prompts.yaml"
    types_file.write_text(agent_types_yaml, encoding="utf-8")
    prompts_file.write_text(prompts_yaml, encoding="utf-8")
    return Settings(
        openrouter_api_key="test",
        agent_types_file=types_file,
        prompts_file=prompts_file,
        _env_file=None,
    )


def test_loads_valid_type(tmp_path):
    settings = _settings(
        tmp_path,
        """
- type_name: assistant
  prompt_key: assistant
  model: deepseek/deepseek-v4-flash
  model_settings: {max_tokens: 256}
""",
        "assistant: You are a test assistant.\n",
    )
    registry = load_registry(settings)
    entry = registry.get("assistant")
    assert entry.agent.name == "assistant"
    assert entry.agent.model == "deepseek/deepseek-v4-flash"
    assert not registry.rejected


def test_rejects_unsupported_capabilities(tmp_path):
    settings = _settings(
        tmp_path,
        """
- type_name: sandboxed
  prompt_key: p
  model: m
  sandbox: true
""",
        "p: text\n",
    )
    registry = load_registry(settings)
    assert "sandboxed" in registry.rejected
    with pytest.raises(UnknownAgentTypeError, match="invalid"):
        registry.get("sandboxed")


def test_rejects_missing_prompt_key(tmp_path):
    settings = _settings(
        tmp_path,
        """
- type_name: assistant
  prompt_key: nope
  model: m
""",
        "assistant: text\n",
    )
    registry = load_registry(settings)
    assert "assistant" in registry.rejected
    assert "prompt_key" in registry.rejected["assistant"]


def test_unknown_type_raises(tmp_path):
    settings = _settings(tmp_path, "[]", "a: b\n")
    registry = load_registry(settings)
    with pytest.raises(UnknownAgentTypeError, match="Unknown"):
        registry.get("ghost")


def test_shipped_config_files_are_valid():
    settings = Settings(openrouter_api_key="test", _env_file=None)
    registry = load_registry(settings)
    assert "assistant" in registry.types
    assert not registry.rejected
