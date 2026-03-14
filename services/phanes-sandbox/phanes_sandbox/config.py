from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    # Container execution defaults
    sandbox_image: str = "phanes-sandbox-runtime:latest"
    sandbox_cpu_limit: float = 1.0
    sandbox_memory_limit_mb: int = 512
    sandbox_timeout_seconds: int = 300
    sandbox_network: str = "none"  # blocked by default

    # Artifact storage
    artifact_output_dir: str = "/tmp/phanes-sandbox/output"

    class Config:
        env_file = ".env"


settings = Settings()
