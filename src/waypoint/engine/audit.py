"""Append-only, structured audit log.

Every scan, plan, install, and rollback goes through here as one JSON-Lines
record. This is the piece SDI has no equivalent of, and it's required for
any real IT-toolchain integration: a technician (or an RMM script) needs to
be able to answer "what did Waypoint actually do on this machine, and when"
without relying on memory or screenshots.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


class AuditLog:
    def __init__(self, log_path: str) -> None:
        self.log_path = Path(log_path)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)

    def record(self, event: str, **fields: Any) -> None:
        entry = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "event": event,
            **fields,
        }
        with open(self.log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, default=str) + "\n")

    def read_all(self) -> list[dict]:
        if not self.log_path.exists():
            return []
        with open(self.log_path, encoding="utf-8") as f:
            return [json.loads(line) for line in f if line.strip()]
