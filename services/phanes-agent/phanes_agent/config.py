from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parent.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    app_env: str = "development"

    # OpenRouter. Key intentionally defaults to empty — bootstrap fails fast
    # with a clear message; real value is injected via environment variable.
    openrouter_api_key: str = ""
    openrouter_base_url: str = "https://openrouter.ai/api/v1"

    # Storage. Async URL is what the service uses; the sync variant (derived)
    # is used by the trace writer thread.
    # Platform MySQL (phanes-task compose on dev; Atlas on prod), schema
    # phanes_agent — single instance, separate schemas.
    database_url: str = (
        "mysql+aiomysql://phanes:phanes_dev@localhost:3306/phanes_agent"
    )
    create_tables: bool = True

    # Phoenix trace debug UI (second pipeline; MySQL stays system-of-record).
    phoenix_enabled: bool = True
    phoenix_collector_endpoint: str = "http://localhost:6006"
    phoenix_project: str = "phanes-agent"

    # Runtime knobs
    max_concurrent_runs: int = 8
    run_timeout_seconds: int = 300

    # AgentType config source — slice 1: local files (phanes-config later).
    agent_types_file: Path = SERVICE_ROOT / "config" / "agent_types.yaml"
    prompts_file: Path = SERVICE_ROOT / "config" / "prompts.yaml"

    @property
    def database_url_sync(self) -> str:
        return self.database_url.replace("+aiomysql", "+pymysql").replace(
            "+aiosqlite", ""
        )
