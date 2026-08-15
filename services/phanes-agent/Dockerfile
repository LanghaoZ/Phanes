FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim

WORKDIR /app

# Dependency layer (cached until pyproject/lock change)
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-install-project --no-dev

COPY phanes_agent ./phanes_agent
COPY scripts ./scripts
RUN uv sync --frozen --no-dev

EXPOSE 8100
# Single worker is REQUIRED: run mailboxes and in-flight runs are
# in-process state (see design).
CMD ["uv", "run", "--no-sync", "uvicorn", "phanes_agent.main:app", "--host", "0.0.0.0", "--port", "8100"]
