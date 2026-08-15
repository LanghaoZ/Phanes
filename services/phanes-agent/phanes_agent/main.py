import asyncio
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from .api import agent_types, ops, runs
from .bootstrap import configure_models, configure_tracing
from .clients.config import ConfigServiceClient
from .clients.prompts import PhoenixPromptResolver
from .config import Settings
from .core.registry import RegistryManager
from .core.runs import RunService
from .logging_setup import setup_logging
from .storage.db import init_db, make_engine, make_sessionmaker

logger = logging.getLogger(__name__)


async def _poll_registry(manager: RegistryManager, interval: float) -> None:
    while True:
        await asyncio.sleep(interval)
        try:
            if await manager.refresh():
                logger.info("Registry reloaded from phanes-config")
        except Exception:
            logger.exception("Registry poll iteration failed")


def create_app(
    settings: Settings | None = None,
    registry_manager: RegistryManager | None = None,
) -> FastAPI:
    settings = settings or Settings()
    setup_logging(settings)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        configure_models(settings)
        trace_processor = configure_tracing(settings)

        engine = make_engine(settings)
        await init_db(engine, settings)
        sessionmaker = make_sessionmaker(engine)

        manager = registry_manager
        config_client: ConfigServiceClient | None = None
        poll_task: asyncio.Task | None = None
        if manager is None:
            config_client = ConfigServiceClient(
                settings.config_service_url, settings.config_namespace
            )
            prompt_resolver = PhoenixPromptResolver(
                settings.phoenix_collector_endpoint,
                settings.resolved_prompt_tag,
                settings.prompt_cache_ttl_seconds,
            )
            manager = RegistryManager(config_client, prompt_resolver)
            await manager.refresh(force=True)  # tolerated failure → empty + flagged
            if not manager.current.types:
                logger.warning(
                    "Registry is empty after initial load — check phanes-config "
                    "and Phoenix; the service is up but no runs will succeed."
                )
            poll_task = asyncio.create_task(
                _poll_registry(manager, settings.config_poll_seconds),
                name="registry-poll",
            )

        app.state.settings = settings
        app.state.sessionmaker = sessionmaker
        app.state.registry_manager = manager
        app.state.run_service = RunService(manager, sessionmaker, settings)

        yield

        if poll_task is not None:
            poll_task.cancel()
        if config_client is not None:
            await config_client.aclose()
        trace_processor.shutdown()
        await engine.dispose()

    app = FastAPI(title="Phanes Agent Layer", version="0.2.0", lifespan=lifespan)
    app.include_router(runs.router)
    app.include_router(agent_types.router)
    app.include_router(ops.router)
    return app


app = create_app()
