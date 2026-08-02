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
    version: int


class AgentTypesOut(BaseModel):
    types: list[AgentTypeOut]
    rejected: dict[str, str]


@router.get("/agent-types")
async def list_agent_types(request: Request) -> AgentTypesOut:
    registry = request.app.state.registry
    return AgentTypesOut(
        types=[
            AgentTypeOut(
                type_name=t.config.type_name,
                enabled=t.config.enabled,
                model=t.config.model,
                conversational=t.config.conversational,
                sandbox=t.config.sandbox,
                memory=t.config.memory,
                version=t.config.version,
            )
            for t in registry.types.values()
        ],
        rejected=dict(registry.rejected),
    )
