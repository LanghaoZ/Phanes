import pytest
from fastapi.testclient import TestClient

from phanes_agent.config import Settings
from phanes_agent.main import create_app


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(
        openrouter_api_key="test-key-not-real",
        database_url=f"sqlite+aiosqlite:///{tmp_path}/test.db",
        phoenix_enabled=False,
        create_tables=True,
        run_timeout_seconds=10,
        _env_file=None,
    )


@pytest.fixture
def client(settings):
    app = create_app(settings)
    with TestClient(app) as c:
        yield c
