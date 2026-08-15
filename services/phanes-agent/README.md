# phanes-agent

Phanes Agent Layer — a thin service on the OpenAI Agents SDK (OpenRouter as
the model API). Design: `designs/phanes_agent_design.md` (repo root,
local-only).

## Scope (slices 1–2)

Run primitive + trace observability + **dynamic configuration**: AgentType
documents live in phanes-config (versioned, hot-reloaded), prompt text
lives in Phoenix Prompts (tag-deployed: dev pulls `development`, prod
pulls `production`). Still pending in later slices: sandbox, multi-agent,
conversations.

## Quickstart

**Full stack in Docker** (repo root; PHANES_API_KEY in shell env):

```bash
docker compose -f deploy/infra.dev.yml up -d     # shared dev infra (once)
docker compose up -d --build                     # config + phoenix + agent
docker compose --profile seed run --rm seed      # first time only
```

**Hybrid dev loop** (infra + siblings in Docker, THIS service on the host
for hot reload):

```bash
docker compose -f deploy/infra.dev.yml up -d
docker compose up -d phanes-config phoenix
cd services/phanes-agent
cp .env.example .env        # PHANES_API_KEY already lives in ~/.zshrc
uv sync && uv run python scripts/seed_dev.py
uv run uvicorn phanes_agent.main:app --port 8100   # single worker — required
```

## Changing config / prompts at runtime

- AgentType docs: `PUT :8200/configs/phanes-agent/agent-types/<name>` —
  picked up by the 15s poll, or immediately via
  `POST :8100/admin/agent-types/reload`. Rollback via phanes-config.
- Prompts: edit in the Phoenix UI (or client), move the environment tag
  to the new version — that IS the deployment. Every run records the
  exact config + prompt versions it executed with (trace metadata).

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
docker exec phanes-mysql \
  mysql -uphanes -pphanes_dev phanes_agent \
  -e 'select run_id, status, input_tokens, output_tokens from runs order by created_at desc limit 5;'
```

## Logs

Two sinks, mirroring the phanes-task Serilog convention:

- **console** — human-readable, for foreground dev
- **`logs/phanes-agent.log`** — JSON lines, daily rotation, 30 days
  retained (gitignored)

```bash
tail -f logs/phanes-agent.log | jq .
jq 'select(.level=="ERROR")' logs/phanes-agent.log
```

Step-level debugging (model inputs/outputs, tool calls, tokens, latency)
lives in the trace pipeline — Phoenix UI or the MySQL `traces`/`spans`
tables — not in these logs. Uvicorn's HTTP access lines stay
console-only for now.

## Notes

- `POST /runs` without `?wait=true` returns 202 immediately; poll
  `GET /runs/{id}`.
- `session_key` gives a recurring job a stable AgentSession (`session_id`
  joins an existing one); plain runs get a fresh one-shot session.
- Run with a single uvicorn worker only — mailboxes and in-flight runs are
  in-process state.
