from typing import Any

from fastapi import APIRouter, Header, HTTPException, Query, Request, Response
from pydantic import BaseModel

from ..storage import ConfigStore, VersionConflictError

router = APIRouter(tags=["configs"])


class PutConfigRequest(BaseModel):
    doc: dict[str, Any]
    author: str | None = None


def _store(request: Request) -> ConfigStore:
    return request.app.state.store


def _etag(version: int) -> str:
    return f'"{version}"'


@router.get("/configs/{namespace}")
async def list_keys(request: Request, namespace: str):
    return {"namespace": namespace, "keys": await _store(request).list_keys(namespace)}


@router.get("/configs/{namespace}/{key:path}/versions")
async def list_versions(request: Request, namespace: str, key: str):
    versions = await _store(request).list_versions(namespace, key)
    if not versions:
        raise HTTPException(status_code=404, detail=f"{namespace}/{key} not found")
    return {"namespace": namespace, "key": key, "versions": versions}


@router.get("/configs/{namespace}/{key:path}")
async def get_config(
    request: Request,
    response: Response,
    namespace: str,
    key: str,
    version: int | None = Query(default=None),
    if_none_match: str | None = Header(default=None),
):
    store = _store(request)
    entry = (
        await store.get_version(namespace, key, version)
        if version is not None
        else await store.get_latest(namespace, key)
    )
    if entry is None:
        raise HTTPException(status_code=404, detail=f"{namespace}/{key} not found")

    etag = _etag(entry["version"])
    if version is None and if_none_match == etag:
        return Response(status_code=304, headers={"ETag": etag})

    response.headers["ETag"] = etag
    return {
        "namespace": entry["namespace"],
        "key": entry["key"],
        "version": entry["version"],
        "doc": entry["doc"],
        "created_at": entry["created_at"],
        "author": entry.get("author"),
    }


@router.put("/configs/{namespace}/{key:path}")
async def put_config(
    request: Request,
    response: Response,
    namespace: str,
    key: str,
    body: PutConfigRequest,
):
    try:
        entry = await _store(request).put(namespace, key, body.doc, body.author)
    except VersionConflictError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    response.headers["ETag"] = _etag(entry["version"])
    return entry


@router.post("/configs/{namespace}/{key:path}/rollback")
async def rollback(
    request: Request,
    namespace: str,
    key: str,
    to: int = Query(ge=1),
):
    store = _store(request)
    target = await store.get_version(namespace, key, to)
    if target is None:
        raise HTTPException(
            status_code=404, detail=f"{namespace}/{key} version {to} not found"
        )
    entry = await store.put(
        namespace, key, target["doc"], author=f"rollback:to={to}"
    )
    return entry
