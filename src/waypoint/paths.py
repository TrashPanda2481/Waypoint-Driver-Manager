"""Standard on-disk locations for the local driver cache and audit log.

Centralized here so the CLI and GUI never disagree about where state
lives — the same "one path, not two implementations" rule that keeps
scan/plan/apply behavior identical between them (see
docs/Architecture.md section 3.4).
"""

from __future__ import annotations

import os
import sys
from pathlib import Path


def _app_data_root() -> Path:
    if sys.platform == "win32":
        base = Path(os.environ.get("PROGRAMDATA", r"C:\ProgramData"))
        return base / "Waypoint"
    return Path.home() / ".local" / "share" / "waypoint"


def default_cache_dir() -> Path:
    return _app_data_root() / "cache"


def default_audit_log_path() -> Path:
    return _app_data_root() / "audit.jsonl"


def default_backup_dir() -> Path:
    return _app_data_root() / "backups"
