"""Append-only versioned config documents in MongoDB.

One collection, `config_versions`:
    {namespace, key, version, doc, created_at, author?}
Unique index on (namespace, key, version). "Latest" = highest version.
"""

from datetime import datetime, timezone
from typing import Any

from motor.motor_asyncio import AsyncIOMotorDatabase
from pymongo import DESCENDING


def utcnow() -> datetime:
    return datetime.now(timezone.utc).replace(tzinfo=None)


class VersionConflictError(Exception):
    pass


class ConfigStore:
    def __init__(self, db: AsyncIOMotorDatabase):
        self._col = db["config_versions"]

    async def ensure_indexes(self) -> None:
        await self._col.create_index(
            [("namespace", 1), ("key", 1), ("version", DESCENDING)], unique=True
        )

    async def get_latest(self, namespace: str, key: str) -> dict[str, Any] | None:
        return await self._col.find_one(
            {"namespace": namespace, "key": key}, sort=[("version", DESCENDING)]
        )

    async def get_version(
        self, namespace: str, key: str, version: int
    ) -> dict[str, Any] | None:
        return await self._col.find_one(
            {"namespace": namespace, "key": key, "version": version}
        )

    async def put(
        self,
        namespace: str,
        key: str,
        doc: dict[str, Any],
        author: str | None = None,
    ) -> dict[str, Any]:
        # Read-latest-then-insert; the unique index catches concurrent writers.
        from pymongo.errors import DuplicateKeyError

        for _ in range(2):
            latest = await self.get_latest(namespace, key)
            next_version = (latest["version"] + 1) if latest else 1
            entry = {
                "namespace": namespace,
                "key": key,
                "version": next_version,
                "doc": doc,
                "created_at": utcnow(),
                "author": author,
            }
            try:
                await self._col.insert_one(entry)
                entry.pop("_id", None)
                return entry
            except DuplicateKeyError:
                continue
        raise VersionConflictError(
            f"Concurrent writes on {namespace}/{key}; retry the request"
        )

    async def list_keys(self, namespace: str) -> list[dict[str, Any]]:
        """Each key with its current (highest) version and timestamp."""
        pipeline = [
            {"$match": {"namespace": namespace}},
            {"$sort": {"version": DESCENDING}},
            {
                "$group": {
                    "_id": "$key",
                    "version": {"$first": "$version"},
                    "created_at": {"$first": "$created_at"},
                }
            },
            {"$sort": {"_id": 1}},
        ]
        return [
            {"key": r["_id"], "version": r["version"], "created_at": r["created_at"]}
            async for r in self._col.aggregate(pipeline)
        ]

    async def list_versions(self, namespace: str, key: str) -> list[dict[str, Any]]:
        cursor = self._col.find(
            {"namespace": namespace, "key": key},
            projection={"_id": 0, "doc": 0},
            sort=[("version", DESCENDING)],
        )
        return [r async for r in cursor]
