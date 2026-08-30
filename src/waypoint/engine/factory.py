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


def build_oem_sources(cache_dir: str | None = None) -> list[DriverSource]:
    """OEM per-device catalog sources (currently: Dell's CatalogPC.cab).

    Deliberately NOT included in `build_default_sources()`. Two reasons:

    1. Cost. `DellCatalogSource.refresh()` downloads and parses a ~57MB
       uncompressed XML catalog — not something to trigger on every default
       scan, consistent with the user instruction to limit credit/resource-
       consuming activity to what's actually needed.
    2. `refresh()` requires network access and is not idempotent-cheap the
       way `LocalCacheSource` is; a caller (CLI flag, GUI settings toggle)
       should opt into it explicitly and control when the catalog is
       refreshed vs. reused from `cache_dir`.

    Only Dell is exposed here because only Dell's per-device catalog
    (`dell_catalog.DellCatalogSource`) implements the `DriverSource`
    protocol (search-by-hardware-ID). Lenovo and Dell's driver-pack
    catalogs, and HP's platform list, are a different shape
    (`ModelDriverPackSource` / platform-support-only) and are not
    `DriverSource`s — see sources/oem/model_pack.py and
    sources/oem/hp_platform.py for why, and expose those through separate
    model-based lookups rather than folding them into this list.

    Callers must call `.refresh()` on the returned source(s) before the
    first `search()` (or point `cache_dir` at a location where a previous
    `refresh()` already ran) — this function only constructs the source,
    it never triggers a download itself.
    """
    from waypoint.sources.oem.dell_catalog import DellCatalogSource

    return [DellCatalogSource(cache_dir or str(default_cache_dir()))]


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
