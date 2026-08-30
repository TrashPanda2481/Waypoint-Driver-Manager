"""Platform backend interface.

Every OS-specific device enumeration/install backend implements this
protocol. Core matching logic and the engine only ever depend on this
interface, never on `pywin32`, `wmi`, or `pyudev` directly — that's what
lets `core/` and `engine/` be unit-tested on any OS via `platform/mock.py`.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol

from waypoint.core.models import Device


class DeviceBackend(Protocol):
    """Enumerates devices and (on the real backends) performs installs."""

    def enumerate_devices(self) -> list[Device]:
        """Return every present device with its current driver binding."""
        ...

    def export_driver_backup(self, device: Device, dest_dir: str) -> str:
        """Back up the currently-bound driver package for `device` to
        `dest_dir`. Must succeed and return the backup path *before* the
        engine is allowed to proceed with an install for that device.
        """
        ...

    def install_driver(self, device: Device, package_path: str, *, dry_run: bool) -> InstallResult:
        """Install the driver package at `package_path` for `device`.

        When `dry_run` is True, backends must not mutate system state — they
        should return the exact command(s) they *would* run so the caller
        can display/log it.
        """
        ...

    def create_restore_point(self, description: str) -> bool:
        """Create an OS-level restore point (Windows System Restore, or a
        Linux-side equivalent snapshot hook). Returns True only on verified
        success — a False here must block the install batch. This directly
        addresses SDI's known failure mode where a restore point silently
        didn't work. See docs/Architecture.md section 3.3.
        """
        ...


@dataclass
class InstallResult:
    success: bool
    commands_run: list[str] = field(default_factory=list)
    message: str = ""
