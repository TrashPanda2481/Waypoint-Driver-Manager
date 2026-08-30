"""In-memory mock backend used by tests and by GUI/CLI development without
real hardware. This is what keeps core/engine logic testable on Linux CI
even though the primary target platform is Windows.
"""

from __future__ import annotations

from waypoint.core.models import Device
from waypoint.platform.base import InstallResult


class MockDeviceBackend:
    def __init__(self, devices: list[Device] | None = None) -> None:
        self._devices = devices or []
        self.backup_calls: list[tuple[str, str]] = []
        self.install_calls: list[tuple[str, str, bool]] = []
        self.restore_point_calls: list[str] = []
        self.restore_point_should_succeed = True

    def enumerate_devices(self) -> list[Device]:
        return list(self._devices)

    def export_driver_backup(self, device: Device, dest_dir: str) -> str:
        self.backup_calls.append((device.instance_id, dest_dir))
        return f"{dest_dir}/{device.instance_id}.backup"

    def install_driver(self, device: Device, package_path: str, *, dry_run: bool) -> InstallResult:
        self.install_calls.append((device.instance_id, package_path, dry_run))
        command = f"pnputil /add-driver {package_path} /install"
        if dry_run:
            return InstallResult(success=True, commands_run=[command], message="dry-run: no changes made")
        return InstallResult(success=True, commands_run=[command], message="installed")

    def create_restore_point(self, description: str) -> bool:
        self.restore_point_calls.append(description)
        return self.restore_point_should_succeed
