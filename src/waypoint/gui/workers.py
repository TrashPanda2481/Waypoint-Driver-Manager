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
    """Runs WaypointEngine.scan() off the UI thread."""

    finished = Signal(list)  # list[DeviceAssessment]
    failed = Signal(str)

    def __init__(self, engine: WaypointEngine) -> None:
        super().__init__()
        self._engine = engine

    def run(self) -> None:
        try:
            assessments: list[DeviceAssessment] = self._engine.scan()
        except Exception as exc:  # noqa: BLE001 — worker boundary: report to UI, never crash the thread
            self.failed.emit(_describe_scan_error(exc))
            return
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
