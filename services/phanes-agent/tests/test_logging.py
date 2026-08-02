import json
import logging

from phanes_agent.config import Settings
from phanes_agent.logging_setup import _CONFIGURED_FLAG, setup_logging


def _fresh_root():
    root = logging.getLogger()
    for h in list(root.handlers):
        root.removeHandler(h)
    if hasattr(root, _CONFIGURED_FLAG):
        delattr(root, _CONFIGURED_FLAG)
    return root


def test_setup_is_idempotent_and_writes_json(tmp_path):
    root = _fresh_root()
    settings = Settings(
        openrouter_api_key="test", log_dir=tmp_path / "logs", _env_file=None
    )

    setup_logging(settings)
    handlers_after_first = len(root.handlers)
    setup_logging(settings)
    assert len(root.handlers) == handlers_after_first == 2

    logging.getLogger("phanes_agent.test").info("你好 log %s", 42)
    for h in root.handlers:
        h.flush()

    log_file = tmp_path / "logs" / "phanes-agent.log"
    line = log_file.read_text(encoding="utf-8").strip().splitlines()[-1]
    entry = json.loads(line)
    assert entry["msg"] == "你好 log 42"
    assert entry["level"] == "INFO"
    assert entry["logger"] == "phanes_agent.test"
    assert "ts" in entry

    _fresh_root()
