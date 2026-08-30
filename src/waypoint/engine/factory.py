"""Single place that decides which backend and sources a real (non-test)
Waypoint session uses. Both the CLI and the GUI call this — if backend
selection lived in two places, they could silently drift apart, which is
exactly the GUI/CLI-disagreement problem docs/Architecture.md section 3.4
calls out about SDI's `-autoinstall` flag vs. its interactive flow.
"""

from __future__ import annotations

import sys

from waypoint.core.models import SignatureType
from waypoint.engine.audit import AuditLog
from waypoint.engine.session import WaypointEngine
from waypoint.paths import default_audit_log_path, default_cache_dir
from waypoint.platform.base import DeviceBackend
from waypoint.sources.base import DriverSource
from waypoint.sources.local_cache import LocalCacheSource


def build_default_backend() -> DeviceBackend:
    """Pick the real backend for the current OS. `wmi`/`pyudev` are only
    imported lazily inside the backend classes themselves, so constructing
    the backend never fails here — only calling enumerate_devices() can,
    and callers (CLI error handling, GUI worker's `failed` signal) are
    responsible for surfacing that.
    """
    if sys.platform == "win32":
        from waypoint.platform.windows import WindowsDeviceBackend

        return WindowsDeviceBackend()
    from waypoint.platform.linux import LinuxDeviceBackend

    return LinuxDeviceBackend()


def build_default_sources(cache_dir: str | None = None) -> list[DriverSource]:
    sources: list[DriverSource] = [LocalCacheSource(cache_dir or str(default_cache_dir()))]
    if sys.platform == "win32":
        from waypoint.sources.windows_update import WindowsUpdateCatalogSource

        sources.append(WindowsUpdateCatalogSource())
    return sources


def build_default_engine(
    *,
    cache_dir: str | None = None,
    audit_log_path: str | None = None,
    min_signature: SignatureType = SignatureType.ATTESTATION,
) -> WaypointEngine:
    backend = build_default_backend()
    sources = build_default_sources(cache_dir)
    audit = AuditLog(audit_log_path or str(default_audit_log_path()))
    return WaypointEngine(backend, sources, audit, min_signature=min_signature)
