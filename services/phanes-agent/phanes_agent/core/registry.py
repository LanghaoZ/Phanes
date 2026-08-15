"""AgentType registry — dynamic config + code-side catalogs.

AgentType documents live in phanes-config (namespace `phanes-agent`, keys
`agent-types/<name>`); prompt TEXT lives in Phoenix Prompts, pulled by
(prompt_key, tag). The RegistryManager polls for config-version changes
and rebuilds Agent objects atomically; on ANY source failure the
last-good registry keeps serving — config/prompt infrastructure being
down never stops agents.
"""

import logging
from dataclasses import dataclass, field
from typing import Any, Protocol

from agents import Agent, ModelSettings
from pydantic import BaseModel, ValidationError

from ..clients.config import AGENT_TYPES_PREFIX, ConfigServiceClient
from ..clients.prompts import PhoenixPromptResolver, ResolvedPrompt

logger = logging.getLogger(__name__)

# Capabilities that exist in the design but are not implemented yet.
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


@dataclass
class RegisteredType:
    config: AgentTypeConfig
    agent: Agent
    config_version: int
    prompt_version_id: str


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


def _validate(config: AgentTypeConfig) -> str | None:
    """Return a rejection reason, or None if the config is valid this slice."""
    for fname in _UNSUPPORTED_FIELDS:
        if getattr(config, fname):
            return f"'{fname}' is not supported yet"
    if config.sandbox or config.memory:
        return "'sandbox'/'memory' are not supported yet"
    if config.continuation_policy is not None:
        return "'continuation_policy' is not supported yet"
    return None


class PromptResolverLike(Protocol):
    async def resolve(self, prompt_key: str) -> ResolvedPrompt: ...
    def invalidate(self) -> None: ...


async def build_registry(
    docs: dict[str, tuple[int, dict]],
    prompt_resolver: PromptResolverLike,
) -> Registry:
    """docs: {'agent-types/<name>': (config_version, doc)}."""
    registry = Registry()

    for key, (version, raw) in docs.items():
        name = key.removeprefix(AGENT_TYPES_PREFIX) or "?"
        try:
            config = AgentTypeConfig.model_validate(raw)
        except ValidationError as exc:
            registry.rejected[name] = f"invalid document: {exc.errors()[0]['msg']}"
            logger.error("AgentType '%s' rejected: %s", name, registry.rejected[name])
            continue

        if config.type_name != name:
            registry.rejected[name] = (
                f"type_name '{config.type_name}' does not match key '{key}'"
            )
            logger.error("AgentType '%s' rejected: %s", name, registry.rejected[name])
            continue

        reason = _validate(config)
        if reason is not None:
            registry.rejected[name] = reason
            logger.error("AgentType '%s' rejected: %s", name, reason)
            continue

        try:
            prompt = await prompt_resolver.resolve(config.prompt_key)
        except Exception as exc:
            registry.rejected[name] = f"prompt resolution failed: {exc}"
            logger.error("AgentType '%s' rejected: %s", name, registry.rejected[name])
            continue

        agent = Agent(
            name=config.type_name,
            instructions=prompt.text,
            model=config.model,
            model_settings=ModelSettings(**config.model_settings),
        )
        registry.types[name] = RegisteredType(
            config=config,
            agent=agent,
            config_version=version,
            prompt_version_id=prompt.version_id,
        )
        logger.info(
            "AgentType '%s' registered. model=%s config_v=%s prompt_v=%s",
            name,
            config.model,
            version,
            prompt.version_id,
        )

    return registry


class RegistryManager:
    """Holds the current registry; polls phanes-config, swaps atomically."""

    def __init__(
        self,
        config_client: ConfigServiceClient,
        prompt_resolver: PhoenixPromptResolver | PromptResolverLike,
    ):
        self._config_client = config_client
        self._prompt_resolver = prompt_resolver
        self._registry = Registry()
        self._known_versions: dict[str, int] = {}

    @property
    def current(self) -> Registry:
        return self._registry

    async def refresh(self, force: bool = False) -> bool:
        """Rebuild if config versions changed (or force). Returns changed."""
        try:
            versions = await self._config_client.list_agent_type_versions()
        except Exception as exc:
            logger.warning(
                "phanes-config unreachable (%s); keeping last-good registry "
                "(%d types)",
                type(exc).__name__,
                len(self._registry.types),
            )
            return False

        if not force and versions == self._known_versions:
            return False

        if force:
            self._prompt_resolver.invalidate()

        docs: dict[str, tuple[int, dict]] = {}
        try:
            for key in versions:
                docs[key] = await self._config_client.get_doc(key)
        except Exception as exc:
            logger.warning(
                "Config doc fetch failed (%s); keeping last-good registry",
                type(exc).__name__,
            )
            return False

        self._registry = await build_registry(docs, self._prompt_resolver)
        self._known_versions = versions
        return True
