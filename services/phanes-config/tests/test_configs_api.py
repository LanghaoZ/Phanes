import pytest
from fastapi.testclient import TestClient
from mongomock_motor import AsyncMongoMockClient

from phanes_config.config import Settings
from phanes_config.main import create_app
from phanes_config.storage import ConfigStore


@pytest.fixture
def client(tmp_path):
    store = ConfigStore(AsyncMongoMockClient()["phanes_config_test"])
    settings = Settings(log_dir=tmp_path / "logs", _env_file=None)
    app = create_app(settings=settings, store=store)
    with TestClient(app) as c:
        yield c


def test_put_creates_versions(client):
    r1 = client.put(
        "/configs/phanes-agent/agent-types/assistant",
        json={"doc": {"model": "a"}, "author": "test"},
    )
    assert r1.status_code == 200
    assert r1.json()["version"] == 1
    assert r1.headers["etag"] == '"1"'

    r2 = client.put(
        "/configs/phanes-agent/agent-types/assistant", json={"doc": {"model": "b"}}
    )
    assert r2.json()["version"] == 2


def test_get_latest_and_specific_version(client):
    client.put("/configs/ns/k", json={"doc": {"v": 1}})
    client.put("/configs/ns/k", json={"doc": {"v": 2}})

    latest = client.get("/configs/ns/k")
    assert latest.json()["doc"] == {"v": 2}
    assert latest.headers["etag"] == '"2"'

    old = client.get("/configs/ns/k?version=1")
    assert old.json()["doc"] == {"v": 1}


def test_etag_304(client):
    client.put("/configs/ns/k", json={"doc": {"v": 1}})
    r = client.get("/configs/ns/k", headers={"If-None-Match": '"1"'})
    assert r.status_code == 304
    changed = client.get("/configs/ns/k", headers={"If-None-Match": '"0"'})
    assert changed.status_code == 200


def test_missing_key_404(client):
    assert client.get("/configs/ns/ghost").status_code == 404
    assert client.get("/configs/ns/ghost/versions").status_code == 404


def test_list_keys(client):
    client.put("/configs/ns/b", json={"doc": {}})
    client.put("/configs/ns/a", json={"doc": {}})
    client.put("/configs/ns/a", json={"doc": {}})
    body = client.get("/configs/ns").json()
    assert [(k["key"], k["version"]) for k in body["keys"]] == [("a", 2), ("b", 1)]


def test_versions_history(client):
    client.put("/configs/ns/k", json={"doc": {"v": 1}, "author": "me"})
    client.put("/configs/ns/k", json={"doc": {"v": 2}})
    body = client.get("/configs/ns/k/versions").json()
    assert [v["version"] for v in body["versions"]] == [2, 1]
    assert "doc" not in body["versions"][0]


def test_rollback(client):
    client.put("/configs/ns/k", json={"doc": {"v": 1}})
    client.put("/configs/ns/k", json={"doc": {"v": 2}})
    r = client.post("/configs/ns/k/rollback?to=1")
    assert r.json()["version"] == 3
    assert client.get("/configs/ns/k").json()["doc"] == {"v": 1}
    assert client.post("/configs/ns/k/rollback?to=99").status_code == 404


def test_slash_keys_roundtrip(client):
    client.put("/configs/phanes-agent/agent-types/expense-analyst", json={"doc": {"x": 1}})
    r = client.get("/configs/phanes-agent/agent-types/expense-analyst")
    assert r.status_code == 200
    assert r.json()["key"] == "agent-types/expense-analyst"


def test_healthz(client):
    assert client.get("/healthz").json()["status"] == "ok"
