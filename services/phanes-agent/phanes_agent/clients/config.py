"""phanes-config REST client (poll-style consumer).

The registry polls `list_agent_type_versions()` and refetches only keys
whose version moved. phanes-config being down must never stop agents —
callers keep their last-good state on any error here.
"""

import httpx

AGENT_TYPES_PREFIX = "agent-types/"


class ConfigServiceClient:
    def __init__(self, base_url: str, namespace: str, timeout: float = 5.0):
        self._namespace = namespace
        self._http = httpx.AsyncClient(base_url=base_url, timeout=timeout)

    async def list_agent_type_versions(self) -> dict[str, int]:
        """Map of agent-type key ('agent-types/<name>') → current version."""
        resp = await self._http.get(f"/configs/{self._namespace}")
        resp.raise_for_status()
        return {
            entry["key"]: entry["version"]
            for entry in resp.json()["keys"]
            if entry["key"].startswith(AGENT_TYPES_PREFIX)
        }

    async def get_doc(self, key: str) -> tuple[int, dict]:
        resp = await self._http.get(f"/configs/{self._namespace}/{key}")
        resp.raise_for_status()
        body = resp.json()
        return body["version"], body["doc"]

    async def aclose(self) -> None:
        await self._http.aclose()
