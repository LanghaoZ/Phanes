from fastapi import APIRouter, Request
from pydantic import BaseModel

router = APIRouter(tags=["agent-types"])


class AgentTypeOut(BaseModel):
    type_name: str
    enabled: bool
    model: str
    conversational: bool
    sandbox: bool
    memory: bool
    config_version: int
    prompt_version_id: str


class AgentTypesOut(BaseModel):
    prompt_tag: str
    types: list[AgentTypeOut]
    rejected: dict[str, str]


@router.get("/agent-types")
async def list_agent_types(request: Request) -> AgentTypesOut:
    registry = request.app.state.registry_manager.current
    return AgentTypesOut(
        prompt_tag=request.app.state.settings.resolved_prompt_tag,
        types=[
            AgentTypeOut(
                type_name=t.config.type_name,
                enabled=t.config.enabled,
                model=t.config.model,
                conversational=t.config.conversational,
                sandbox=t.config.sandbox,
                memory=t.config.memory,
                config_version=t.config_version,
                prompt_version_id=t.prompt_version_id,
            )
            for t in registry.types.values()
        ],
        rejected=dict(registry.rejected),
    )


@router.post("/admin/agent-types/reload")
async def reload_agent_types(request: Request):
    changed = await request.app.state.registry_manager.refresh(force=True)
    registry = request.app.state.registry_manager.current
    return {
        "reloaded": changed,
        "types": sorted(registry.types.keys()),
        "rejected": dict(registry.rejected),
    }
