"""Service logging — mirrors the phanes-task Serilog convention:

- console: human-readable plaintext (dev foreground)
- file:    structured JSON lines, daily rotation, 30 days retained
           (logs/phanes-agent.log + dated rollovers; logs/ is gitignored)

Step-level debugging belongs to the trace pipeline (Phoenix / MySQL spans);
these logs cover service-level events: startup, config rejections, run
failures, infrastructure errors.
"""

import json
import logging
import sys
from datetime import datetime, timezone
from logging.handlers import TimedRotatingFileHandler

from .config import Settings

_CONFIGURED_FLAG = "_phanes_agent_logging_configured"


class JsonLineFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        entry = {
            "ts": datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(
                timespec="milliseconds"
            ),
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
        }
        if record.exc_info and record.exc_info[0] is not None:
            entry["exc"] = self.formatException(record.exc_info)
        return json.dumps(entry, ensure_ascii=False)


def setup_logging(settings: Settings) -> None:
    root = logging.getLogger()
    if getattr(root, _CONFIGURED_FLAG, False):
        return
    setattr(root, _CONFIGURED_FLAG, True)

    root.setLevel(settings.log_level.upper())

    console = logging.StreamHandler(sys.stderr)
    console.setFormatter(
        logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s")
    )
    root.addHandler(console)

    settings.log_dir.mkdir(parents=True, exist_ok=True)
    file_handler = TimedRotatingFileHandler(
        settings.log_dir / "phanes-agent.log",
        when="midnight",
        backupCount=settings.log_retention_days,
        encoding="utf-8",
        utc=True,
    )
    file_handler.setFormatter(JsonLineFormatter())
    root.addHandler(file_handler)
