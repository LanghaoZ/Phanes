FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim

WORKDIR /app

# Dependency layer (cached until pyproject/lock change)
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-install-project --no-dev

COPY phanes_config ./phanes_config
RUN uv sync --frozen --no-dev

EXPOSE 8200
CMD ["uv", "run", "--no-sync", "uvicorn", "phanes_config.main:app", "--host", "0.0.0.0", "--port", "8200"]
