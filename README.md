# Phanes

**Phanes** is a personal execution platform for running AI-driven workflows, analytics, and background automation on local infrastructure.

---

## Services

### phanes-task
Async task execution framework built on .NET 8. Consumes task events from Kafka and routes them to per-task actors. Exposes an HTTP API for task submission and status queries.

**Stack:** .NET 8, ASP.NET Core, Kafka (Confluent.Kafka), Redis, MySQL

### phanes-agent
AI orchestration service built on Python and LangGraph. Accepts task requests, classifies intent, generates execution plans, and routes steps to appropriate executors. Delegates sandbox execution to phanes-sandbox and durable async work to phanes-task.

**Stack:** Python 3.12, FastAPI, LangGraph, OpenRouter, Claude API

### phanes-sandbox
Secure code execution service. Accepts execution requests via HTTP, runs them in isolated Docker containers, collects outputs, and returns artifact references.

**Stack:** Python 3.12, FastAPI, Docker SDK

### phanes-control-plane
Internal management GUI for monitoring and managing tasks, agent workflows, and system state.

**Stack:** TypeScript, Next.js

---

## Repository Structure

```
phanes/
├── services/
│   ├── phanes-task/
│   ├── phanes-agent/
│   ├── phanes-sandbox/
│   └── phanes-control-plane/
├── scripts/
│   ├── db/             database bootstrap scripts
│   └── dev/            local dev utilities
└── packages/           generated API clients / SDKs (future)
```

---

## Status

Early-stage, under active development.
