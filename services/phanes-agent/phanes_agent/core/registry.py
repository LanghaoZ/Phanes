"""AgentType registry.

Slice 1: loads AgentType config documents from a local YAML file (the same
document shape phanes-config will serve later) and prompt texts from a local
prompts file (Phoenix Prompts later). Builds SDK Agent objects once; invalid
configs are rejected with a reason and never served.
"""

import logging
from dataclasses import dataclass, field
from typing import Any

import yaml
from agents import Agent, ModelSettings
from pydantic import BaseModel, ValidationError

from ..config import Settings

logger = logging.getLogger(__name__)

# Capabilities that exist in the design but are not implemented in slice 1.
# A config that asks for them is rejected (better than silently ignoring).
_UNSUPPORTED_FIELDS = (
    "tools",
    "mcp_servers",
    "skills",
    "collaborators",
    "dispatch_targets",
)


class AgentTypeConfig(BaseModel):
    type_name: str
    enabled: bool = True
    prompt_key: str
    model: str
    model_settings: dict[str, Any] = {}
    tools: list[str] = []
    mcp_servers: list[str] = []
    skills: list[str] = []
    sandbox: bool = False
    memory: bool = False
    conversational: bool = False
    collaborators: list[str] = []
    dispatch_targets: list[str] = []
    continuation_policy: str | None = None
    version: int = 1


@dataclass
class RegisteredType:
    config: AgentTypeConfig
    agent: Agent


@dataclass
class Registry:
    types: dict[str, RegisteredType] = field(default_factory=dict)
    rejected: dict[str, str] = field(default_factory=dict)

    def get(self, type_name: str) -> RegisteredType:
        entry = self.types.get(type_name)
        if entry is None:
            reason = self.rejected.get(type_name)
            if reason:
                raise UnknownAgentTypeError(
                    f"Agent type '{type_name}' is registered but invalid: {reason}"
                )
            raise UnknownAgentTypeError(f"Unknown agent type '{type_name}'")
        if not entry.config.enabled:
            raise UnknownAgentTypeError(f"Agent type '{type_name}' is disabled")
        return entry


class UnknownAgentTypeError(Exception):
    pass


def _validate(config: AgentTypeConfig, prompts: dict[str, str]) -> str | None:
    """Return a rejection reason, or None if the config is valid for slice 1."""
    for fname in _UNSUPPORTED_FIELDS:
        if getattr(config, fname):
            return f"'{fname}' is not supported yet (slice 1)"
    if config.sandbox or config.memory:
        return "'sandbox'/'memory' are not supported yet (slice 1)"
    if config.continuation_policy is not None:
        return "'continuation_policy' is not supported yet (slice 1)"
    if config.prompt_key not in prompts:
        return f"prompt_key '{config.prompt_key}' not found in prompts file"
    return None


def load_registry(settings: Settings) -> Registry:
    registry = Registry()

    with open(settings.prompts_file, encoding="utf-8") as f:
        prompts: dict[str, str] = yaml.safe_load(f) or {}
    with open(settings.agent_types_file, encoding="utf-8") as f:
        raw_docs = yaml.safe_load(f) or []

    for raw in raw_docs:
        name = str(raw.get("type_name", "?")) if isinstance(raw, dict) else "?"
        try:
            config = AgentTypeConfig.model_validate(raw)
        except ValidationError as exc:
            registry.rejected[name] = f"invalid document: {exc.errors()[0]['msg']}"
            logger.error("AgentType '%s' rejected: %s", name, registry.rejected[name])
            continue

        reason = _validate(config, prompts)
        if reason is not None:
            registry.rejected[config.type_name] = reason
            logger.error("AgentType '%s' rejected: %s", config.type_name, reason)
            continue

        agent = Agent(
            name=config.type_name,
            instructions=prompts[config.prompt_key],
            model=config.model,
            model_settings=ModelSettings(**config.model_settings),
        )
        registry.types[config.type_name] = RegisteredType(config=config, agent=agent)
        logger.info(
            "AgentType '%s' registered. model=%s version=%s",
            config.type_name,
            config.model,
            config.version,
        )

    return registry
