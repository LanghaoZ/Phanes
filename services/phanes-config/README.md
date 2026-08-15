# phanes-config

Phanes platform dynamic-config plane: versioned JSON config documents over
REST. Design: `designs/phanes_config_design.md` (repo root, local-only).
Deliberately tiny — no caching, no push, no schema validation (consumers
validate their own configs).

## Run (dev)

In Docker (repo root): `docker compose up -d phanes-config` (MongoDB comes
from `deploy/infra.dev.yml`). On the host for hot reload:

```bash
docker compose -f deploy/infra.dev.yml up -d mongo
cd services/phanes-config
uv sync
uv run uvicorn phanes_config.main:app --port 8200
```

## API

    GET  /configs/{ns}                      keys + current versions
    GET  /configs/{ns}/{key}                latest (ETag: version; honors If-None-Match → 304)
         ?version=N                         that version
    PUT  /configs/{ns}/{key}                {"doc": {...}, "author": "..."} → new version
    GET  /configs/{ns}/{key}/versions       history (metadata only)
    POST /configs/{ns}/{key}/rollback?to=N  re-append old doc as new version
    GET  /healthz

Keys may contain slashes (`agent-types/assistant`). Namespace = consuming
service (`phanes-agent`, later `phanes-task`).

## Example

```bash
curl -s -X PUT localhost:8200/configs/phanes-agent/agent-types/assistant \
  -H 'content-type: application/json' \
  -d '{"doc": {"type_name": "assistant", "model": "deepseek/deepseek-v4-flash"}, "author": "langhao"}'

curl -s localhost:8200/configs/phanes-agent/agent-types/assistant | jq .version
curl -s -H 'If-None-Match: "1"' -o /dev/null -w '%{http_code}\n' \
  localhost:8200/configs/phanes-agent/agent-types/assistant   # → 304
```
