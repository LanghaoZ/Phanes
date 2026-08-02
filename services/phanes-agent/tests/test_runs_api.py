import asyncio
import time
from types import SimpleNamespace

import pytest


def _fake_result(output: str = "pong"):
    return SimpleNamespace(
        final_output=output,
        context_wrapper=SimpleNamespace(
            usage=SimpleNamespace(input_tokens=3, output_tokens=5)
        ),
    )


class FakeRunner:
    delay = 0.0
    active = 0
    max_active = 0

    @classmethod
    def reset(cls, delay: float = 0.0):
        cls.delay = delay
        cls.active = 0
        cls.max_active = 0

    @classmethod
    async def run(cls, agent, input, *, run_config=None):
        cls.active += 1
        cls.max_active = max(cls.max_active, cls.active)
        try:
            if cls.delay:
                await asyncio.sleep(cls.delay)
            return _fake_result(f"echo: {input}")
        finally:
            cls.active -= 1


@pytest.fixture
def fake_runner(monkeypatch):
    FakeRunner.reset()
    monkeypatch.setattr("phanes_agent.core.runs.Runner", FakeRunner)
    return FakeRunner


def _poll_until_done(client, run_id: str, timeout: float = 5.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        body = client.get(f"/runs/{run_id}").json()
        if body["status"] in ("succeeded", "failed", "cancelled"):
            return body
        time.sleep(0.02)
    raise AssertionError(f"run {run_id} did not finish in {timeout}s: {body}")


def test_run_wait_true(client, fake_runner):
    resp = client.post(
        "/runs?wait=true", json={"agent_type": "assistant", "input": "hello"}
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "succeeded"
    assert body["final_output"] == "echo: hello"
    assert body["input_tokens"] == 3
    assert body["output_tokens"] == 5
    assert body["trace_id"].startswith("trace_")
    assert body["session_id"]


def test_run_async_then_poll(client, fake_runner):
    resp = client.post("/runs", json={"agent_type": "assistant", "input": "hi"})
    assert resp.status_code == 202
    body = _poll_until_done(client, resp.json()["run_id"])
    assert body["status"] == "succeeded"
    assert body["final_output"] == "echo: hi"


def test_unknown_agent_type_404(client, fake_runner):
    resp = client.post("/runs", json={"agent_type": "ghost", "input": "x"})
    assert resp.status_code == 404


def test_unknown_session_id_404(client, fake_runner):
    resp = client.post(
        "/runs",
        json={"agent_type": "assistant", "input": "x", "session_id": "NOPE"},
    )
    assert resp.status_code == 404


def test_both_session_params_422(client, fake_runner):
    resp = client.post(
        "/runs",
        json={
            "agent_type": "assistant",
            "input": "x",
            "session_id": "a",
            "session_key": "b",
        },
    )
    assert resp.status_code == 422


def test_session_key_reuses_session(client, fake_runner):
    first = client.post(
        "/runs?wait=true",
        json={"agent_type": "assistant", "input": "1", "session_key": "daily-job"},
    ).json()
    second = client.post(
        "/runs?wait=true",
        json={"agent_type": "assistant", "input": "2", "session_key": "daily-job"},
    ).json()
    assert first["session_id"] == second["session_id"]


def test_same_pair_runs_are_serialized(client, fake_runner):
    fake_runner.reset(delay=0.05)
    ids = [
        client.post(
            "/runs",
            json={"agent_type": "assistant", "input": str(i), "session_key": "serial"},
        ).json()["run_id"]
        for i in range(3)
    ]
    for run_id in ids:
        assert _poll_until_done(client, run_id)["status"] == "succeeded"
    assert fake_runner.max_active == 1


def test_agent_types_endpoint(client, fake_runner):
    body = client.get("/agent-types").json()
    names = [t["type_name"] for t in body["types"]]
    assert "assistant" in names
    assert body["rejected"] == {}


def test_healthz(client, fake_runner):
    body = client.get("/healthz").json()
    assert body["status"] == "ok"
    assert "assistant" in body["checks"]["agent_types"]
