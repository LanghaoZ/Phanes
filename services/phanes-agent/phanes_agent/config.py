from pathlib import Path

from pydantic import AliasChoices, Field
from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parent.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    app_env: str = "development"

    # OpenRouter. Key intentionally defaults to empty — bootstrap fails fast
    # with a clear message; real value is injected via environment variable.
    # PHANES_API_KEY is the user's convention (~/.zshrc); OPENROUTER_API_KEY
    # also accepted.
    openrouter_api_key: str = Field(
        default="",
        validation_alias=AliasChoices("phanes_api_key", "openrouter_api_key"),
    )
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

    # Logging: console plaintext + rolling JSON file (phanes-task parity).
    log_level: str = "INFO"
    log_dir: Path = SERVICE_ROOT / "logs"
    log_retention_days: int = 30

    # AgentType config source: phanes-config service.
    config_service_url: str = "http://localhost:8200"
    config_namespace: str = "phanes-agent"
    config_poll_seconds: float = 15.0

    # Prompt source: Phoenix Prompts (same Phoenix as the trace UI).
    # Tag defaults by environment: development pulls `development`,
    # anything else pulls `production`.
    prompt_tag: str | None = None
    prompt_cache_ttl_seconds: float = 300.0

    @property
    def resolved_prompt_tag(self) -> str:
        if self.prompt_tag:
            return self.prompt_tag
        return "development" if self.app_env == "development" else "production"

    @property
    def database_url_sync(self) -> str:
        return self.database_url.replace("+aiomysql", "+pymysql").replace(
            "+aiosqlite", ""
        )
