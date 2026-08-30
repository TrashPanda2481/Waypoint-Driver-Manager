"""Minimal streaming HTTP download, stdlib-only.

Waypoint's core has zero third-party dependencies by design (see
pyproject.toml `dependencies = []`) — OEM catalog fetching is real network
I/O, not something to pull in `requests` for. `download_file` is injected
into the OEM sources as a callable so tests never need real network access.
"""

from __future__ import annotations

import urllib.request
from pathlib import Path

DEFAULT_TIMEOUT_SECONDS = 60
_USER_AGENT = "Waypoint-Driver-Manager/0.1 (+https://github.com/TrashPanda2481/Waypoint-Driver-Manager)"


def download_file(url: str, dest_path: str | Path, *, timeout: float = DEFAULT_TIMEOUT_SECONDS) -> Path:
    """Stream `url` to `dest_path`. Raises `urllib.error.URLError` /
    `urllib.error.HTTPError` on failure — callers decide how to surface
    that (CLI error message, GUI worker `failed` signal), consistent with
    how `windows_update.py` leaves error handling to its caller."""
    dest_path = Path(dest_path)
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT})
    with urllib.request.urlopen(request, timeout=timeout) as response, open(dest_path, "wb") as out_file:
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            out_file.write(chunk)
    return dest_path
