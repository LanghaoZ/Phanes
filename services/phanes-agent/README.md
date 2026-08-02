# phanes-agent

Phanes Agent Layer — a thin service on the OpenAI Agents SDK (OpenRouter as
the model API). Design: `designs/phanes_agent_design.md` (repo root,
local-only).

## Slice 1 scope

Run primitive + trace observability. One `assistant` agent type (no tools,
no sandbox). Everything else (phanes-config, Phoenix Prompts, sandbox,
multi-agent, conversations) lands in later slices.

## Quickstart (dev, everything local)

```bash
cd services/phanes-agent

# 1. Dev dependencies: MySQL + Phoenix
docker compose up -d

# 2. Environment
cp .env.example .env
export OPENROUTER_API_KEY=sk-or-...   # or edit .env; never committed

# 3. Install & run (single worker — required, in-process state)
uv sync
uv run uvicorn phanes_agent.main:app --port 8100
```

## Acceptance walkthrough

```bash
# health
curl -s localhost:8100/healthz | jq

# registered types
curl -s localhost:8100/agent-types | jq

# a run, waiting for the result
curl -s -X POST 'localhost:8100/runs?wait=true' \
  -H 'content-type: application/json' \
  -d '{"agent_type": "assistant", "input": "用一句话介绍你自己"}' | jq

# inspect it again by id
curl -s localhost:8100/runs/<run_id> | jq
```

Then open Phoenix at http://localhost:6006 → project `phanes-agent` → the
run's trace (span waterfall, input/output, tokens, latency). MySQL holds the
system-of-record copy:

```bash
docker exec -it phanes-agent-mysql \
  mysql -uphanes -pphanes phanes_agent \
  -e 'select run_id, status, input_tokens, output_tokens from runs order by created_at desc limit 5;'
```

## Notes

- `POST /runs` without `?wait=true` returns 202 immediately; poll
  `GET /runs/{id}`.
- `session_key` gives a recurring job a stable AgentSession (`session_id`
  joins an existing one); plain runs get a fresh one-shot session.
- Run with a single uvicorn worker only — mailboxes and in-flight runs are
  in-process state.
