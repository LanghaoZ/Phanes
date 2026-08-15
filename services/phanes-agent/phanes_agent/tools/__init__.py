# Tool catalog — the code-side extension point.
# Slice 1 ships no tools; the catalog exists so AgentType configs can be
# validated against it from day one.

_CATALOG: dict[str, object] = {}


def tool(name: str):
    """Register a function tool implementation under a stable name."""

    def decorator(fn):
        _CATALOG[name] = fn
        return fn

    return decorator


def get_tool(name: str):
    return _CATALOG[name]


def known_tools() -> set[str]:
    return set(_CATALOG)
