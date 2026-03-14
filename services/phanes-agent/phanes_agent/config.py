from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    # LLM
    openrouter_api_key: str = ""
    anthropic_api_key: str = ""

    # Phanes-Task API
    task_api_base_url: str = "http://localhost:5000"

    # Sandbox
    sandbox_image: str = "phanes-sandbox:latest"
    sandbox_cpu_limit: float = 1.0
    sandbox_memory_limit_mb: int = 512
    sandbox_timeout_seconds: int = 300

    class Config:
        env_file = ".env"


settings = Settings()
