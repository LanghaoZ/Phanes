from contextlib import asynccontextmanager

from fastapi import FastAPI

from .api import agent_types, ops, runs
from .bootstrap import configure_models, configure_tracing
from .config import Settings
from .core.registry import load_registry
from .core.runs import RunService
from .logging_setup import setup_logging
from .storage.db import init_db, make_engine, make_sessionmaker


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings()
    setup_logging(settings)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        configure_models(settings)
        trace_processor = configure_tracing(settings)

        engine = make_engine(settings)
        await init_db(engine, settings)
        sessionmaker = make_sessionmaker(engine)
        registry = load_registry(settings)

        app.state.settings = settings
        app.state.sessionmaker = sessionmaker
        app.state.registry = registry
        app.state.run_service = RunService(registry, sessionmaker, settings)

        yield

        trace_processor.shutdown()
        await engine.dispose()

    app = FastAPI(title="Phanes Agent Layer", version="0.1.0", lifespan=lifespan)
    app.include_router(runs.router)
    app.include_router(agent_types.router)
    app.include_router(ops.router)
    return app


app = create_app()
