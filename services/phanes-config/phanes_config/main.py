from contextlib import asynccontextmanager

from fastapi import FastAPI
from motor.motor_asyncio import AsyncIOMotorClient

from .api import configs
from .config import Settings
from .logging_setup import setup_logging
from .storage import ConfigStore


def create_app(settings: Settings | None = None, store: ConfigStore | None = None):
    settings = settings or Settings()
    setup_logging(settings)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if store is not None:
            app.state.store = store  # injected by tests
            client = None
        else:
            client = AsyncIOMotorClient(settings.mongo_url)
            app.state.store = ConfigStore(client[settings.mongo_database])
        await app.state.store.ensure_indexes()
        app.state.settings = settings
        yield
        if client is not None:
            client.close()

    app = FastAPI(title="Phanes Config", version="0.1.0", lifespan=lifespan)
    app.include_router(configs.router)

    @app.get("/healthz")
    async def healthz():
        try:
            await app.state.store.list_keys("_healthcheck")
            return {"status": "ok"}
        except Exception as exc:
            return {"status": "degraded", "error": type(exc).__name__}

    return app


app = create_app()
