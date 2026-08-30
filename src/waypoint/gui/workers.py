"""QThread workers for GUI operations that must not block the event loop.

WMI device enumeration and driver-source lookups can take long enough on
real hardware/fleets that running them synchronously from a button click
would freeze the window. Pattern: a QObject worker holds the blocking call,
gets moved to a QThread, and only ever talks back to the main thread via
signals — never touches widgets directly.
"""

from __future__ import annotations

from PySide6.QtCore import QObject, Signal

from waypoint.core.models import DeviceAssessment
from waypoint.engine.session import WaypointEngine


class ScanWorker(QObject):
    """Runs WaypointEngine.scan() off the UI thread.

    If `include_oem` is set, this also builds and refreshes the OEM catalog
    sources (`engine.factory.build_oem_sources()`) here on the background
    thread — the GUI equivalent of the CLI's `--oem` flag (see
    cli/main.py). This matters because a first-time refresh downloads a
    real ~57MB catalog; doing that on the UI thread would freeze the
    window exactly the way real-hardware WMI enumeration would if it ran
    there instead of in this worker.

    The OEM sources are added to `engine.sources` only for the duration of
    this one `scan()` call and removed again in `finally`, regardless of
    outcome — so toggling the checkbox off before the next scan genuinely
    takes effect, and OEM sources are never silently duplicated onto
    `engine.sources` across repeated scans with the checkbox left on.

    `force_oem_refresh` is the GUI equivalent of the CLI's
    `--force-oem-refresh`: forces a re-download of the OEM catalog even
    if a cached copy already exists, instead of the default `force=False`
    behavior of reusing whatever is on disk. Has no effect unless
    `include_oem` is also set.
    """

    finished = Signal(list)  # list[DeviceAssessment]
    failed = Signal(str)

    def __init__(
        self,
        engine: WaypointEngine,
        *,
        include_oem: bool = False,
        oem_cache_dir: str | None = None,
        force_oem_refresh: bool = False,
    ) -> None:
        super().__init__()
        self._engine = engine
        self._include_oem = include_oem
        self._oem_cache_dir = oem_cache_dir
        self._force_oem_refresh = force_oem_refresh

    def run(self) -> None:
        original_sources = list(self._engine.sources)
        try:
            if self._include_oem:
                from waypoint.engine.factory import build_oem_sources

                oem_sources = build_oem_sources(self._oem_cache_dir)
                for source in oem_sources:
                    # force=False (default): only downloads if this
                    # cache_dir has never been refreshed before, see
                    # cli/main.py's identical --force-oem-refresh comment.
                    # force=True (force_oem_refresh checkbox): re-download
                    # regardless of what's already cached.
                    source.refresh(force=self._force_oem_refresh)
                self._engine.sources = original_sources + oem_sources
            assessments: list[DeviceAssessment] = self._engine.scan()
        except Exception as exc:  # noqa: BLE001 — worker boundary: report to UI, never crash the thread
            self.failed.emit(_describe_scan_error(exc))
            return
        finally:
            self._engine.sources = original_sources
        self.finished.emit(assessments)


def _describe_scan_error(exc: Exception) -> str:
    """Turn a raw exception into something a technician can act on,
    especially the common "not actually on Windows" / "pywin32 missing"
    case during development off the target platform.
    """
    module = type(exc).__module__
    if isinstance(exc, ModuleNotFoundError):
        return (
            f"Missing dependency: {exc}. Install the platform extra "
            f"(`pip install \"waypoint-driver-manager[windows]\"` on Windows, "
            f"`[linux]` on Linux) and try again."
        )
    return f"{module}.{type(exc).__name__}: {exc}"
