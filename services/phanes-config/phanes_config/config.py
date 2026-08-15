from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parent.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    app_env: str = "development"

    # Platform MongoDB (docker-compose here on dev; Atlas container on prod).
    mongo_url: str = "mongodb://localhost:27017"
    mongo_database: str = "phanes_config"

    # Logging (platform convention: console + rolling JSON file)
    log_level: str = "INFO"
    log_dir: Path = SERVICE_ROOT / "logs"
    log_retention_days: int = 30
