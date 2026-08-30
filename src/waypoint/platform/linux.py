"""Linux device backend.

Scoped narrowly per docs/Architecture.md section 6 ("Open Decisions"): this
exists mainly to keep `core`/`engine` genuinely cross-platform and testable,
not as a full parity implementation of Windows-style driver management.
Linux driver "installation" is a fundamentally different problem (kernel
modules + firmware blobs are managed by the distro/kernel, not INF
packages) — this backend reports what's bound today via udev/lspci and is
a stretch goal, not the primary target.
"""

from __future__ import annotations

import subprocess

from waypoint.core.models import Device, InstalledDriver, SignatureType
from waypoint.platform.base import InstallResult


class LinuxDeviceBackend:
    def enumerate_devices(self) -> list[Device]:
        import pyudev  # type: ignore  # imported lazily: Linux-only dependency

        context = pyudev.Context()
        devices: list[Device] = []
        for dev in context.list_devices(subsystem="pci"):
            hwid = dev.get("PCI_ID")
            driver = dev.get("DRIVER")
            devices.append(
                Device(
                    hwids=(hwid,) if hwid else (),
                    class_guid=dev.get("PCI_CLASS", ""),
                    class_name="PCI",
                    friendly_name=dev.sys_name,
                    instance_id=dev.sys_path,
                    problem_code=None if driver else 28,
                    installed=InstalledDriver(
                        version="kernel-bundled",
                        driver_date=None,
                        publisher="Linux kernel",
                        signature_type=SignatureType.WHQL,
                    )
                    if driver
                    else None,
                )
            )
        return devices

    def export_driver_backup(self, device: Device, dest_dir: str) -> str:
        # Kernel modules aren't "exported" the way Windows driver packages
        # are; recorded here for interface parity only.
        raise NotImplementedError("Driver backup is not applicable to Linux kernel modules.")

    def install_driver(self, device: Device, package_path: str, *, dry_run: bool) -> InstallResult:
        command = f"modprobe {package_path}"
        if dry_run:
            return InstallResult(success=True, commands_run=[command], message="dry-run: no changes made")
        result = subprocess.run(["modprobe", package_path], capture_output=True, text=True, check=False)
        return InstallResult(success=result.returncode == 0, commands_run=[command], message=result.stderr)

    def create_restore_point(self, description: str) -> bool:
        # No universal equivalent; a Btrfs/LVM snapshot hook is a reasonable
        # future extension point but out of scope for v1.
        return False
