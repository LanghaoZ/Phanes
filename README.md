# Phanes

**Phanes** is a personal execution platform for running AI-driven workflows,
analytics, and background automation on local infrastructure.

---

## Quickstart (dev)

Everything in Docker (PHANES_API_KEY must be in the shell env or a
repo-root `.env`):

```bash
docker compose -f deploy/infra.dev.yml up -d    # shared dev infra: MySQL/Mongo/Redis/Kafka (once)
docker compose up -d --build                    # project services: config + agent + phoenix
docker compose --profile seed run --rm seed     # first time only: seed prompt + agent-type config
```

Smoke test:

```bash
curl -s -X POST 'localhost:8100/runs?wait=true' -H 'content-type: application/json' \
  -d '{"agent_type": "assistant", "input": "hello"}' | jq
```

Consoles: Phoenix (traces + prompt management) http://localhost:6006 ·
phanes-config API docs http://localhost:8200/docs · agent health
http://localhost:8100/healthz

Hybrid dev loop (infra in Docker, the service you're editing on the host
with hot reload): see each service's README.

**Ownership rule:** the root `docker-compose.yml` orchestrates only
project-owned services. Databases/messaging are machine-level shared
infra — `deploy/infra.dev.yml` provisions them for dev and doubles as
server-rebuild documentation; on the server (Atlas) the already-running
containers are used via env overrides, never orchestrated by this project.

---

## Services

### phanes-agent
Agent Layer: a thin service on the OpenAI Agents SDK (OpenRouter /
Responses API). Run primitive (`POST /runs`), AgentSessions, per-type
dynamic configuration, dual trace pipeline (MySQL system-of-record +
Phoenix debug UI).

**Stack:** Python 3.12+, FastAPI, openai-agents SDK, OpenRouter, MySQL, Phoenix

### phanes-config
Platform dynamic-config plane: append-only versioned JSON config
documents over REST (history, rollback, ETag polling). First consumer:
phanes-agent's AgentType registry.

**Stack:** Python 3.12+, FastAPI, MongoDB

### phanes-task
Async task execution framework built on .NET 8. Consumes task events from
Kafka and routes them to per-task actors. Exposes an HTTP API for task
submission and status queries.

**Stack:** .NET 8, ASP.NET Core, Kafka (Confluent.Kafka), Redis, MySQL

### phanes-sandbox / phanes-control-plane
Early scaffolds; current direction: sandboxing is delivered through the
Agents SDK's Docker sandbox inside phanes-agent (see designs/), and the
management GUI is deferred.

---

## Repository Structure

```
phanes/
├── docker-compose.yml      project services (config + agent + phoenix + seed)
├── deploy/
│   ├── infra.dev.yml       shared dev infra (dev-only; server-rebuild doc)
│   └── mysql-init.sql      provisions all Phanes schemas
├── services/
│   ├── phanes-agent/
│   ├── phanes-config/
│   ├── phanes-task/
│   ├── phanes-sandbox/
│   └── phanes-control-plane/
├── contracts/              proto definitions
└── designs/                design docs (local-only, gitignored)
```

---

## Status

Early-stage, under active development. Agent Layer slices 1–2.5 landed
(run primitive, dynamic config, Phoenix prompts, containerization);
sandbox / multi-agent / conversations upcoming.
