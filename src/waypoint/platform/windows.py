"""Windows device backend.

Built against documented, real Windows tooling — not a placeholder API.
This module cannot be executed outside Windows (it imports `wmi`/`pywin32`
lazily inside methods, not at module scope, so the package still imports
cleanly on Linux for testing/CI). It has not yet been run against real
hardware; treat as a first implementation pass to validate on the Dell/Asus
dev machines the same way Meridian OS components are validated before being
called "working."

References:
- Device enumeration: WMI `Win32_PnPEntity` / `Win32_PnPSignedDriver`.
- Install: `pnputil /add-driver <inf> /install`
  https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples
- Backup: `pnputil /export-driver <published name> <dest>`
- Restore point: `SystemRestore.CreateRestorePoint` via WMI (`SystemRestore`
  class on `root\\default` namespace), verified by checking the return code.
"""

from __future__ import annotations

import subprocess
from datetime import datetime

from waypoint.core.models import Device, InstalledDriver, SignatureType
from waypoint.platform.base import InstallResult

_SIGNATURE_MAP = {
    # Win32_PnPSignedDriver.DriverSignForm / DigitalSigner text does not map
    # 1:1 to WHQL vs attestation; that distinction needs `Get-WindowsDriver`
    # or catalog inspection. This mapping is a first pass — flagged as an
    # open item in docs/Architecture.md rather than silently guessed.
    True: SignatureType.WHQL,
    False: SignatureType.UNSIGNED,
}


class WindowsDeviceBackend:
    def __init__(self) -> None:
        self._wmi = None  # lazily initialized in enumerate_devices()

    def _connect(self):
        import wmi  # type: ignore  # imported lazily: Windows-only dependency

        if self._wmi is None:
            self._wmi = wmi.WMI()
        return self._wmi

    def enumerate_devices(self) -> list[Device]:
        conn = self._connect()
        devices: list[Device] = []
        for entry in conn.Win32_PnPEntity():
            hwids = tuple(entry.HardwareID or ())
            if not hwids:
                continue
            installed = self._lookup_installed_driver(conn, entry.DeviceID)
            devices.append(
                Device(
                    hwids=hwids,
                    class_guid=entry.ClassGuid or "",
                    class_name=entry.PNPClass or "Unknown",
                    friendly_name=entry.Name or entry.DeviceID,
                    instance_id=entry.DeviceID,
                    problem_code=int(entry.ConfigManagerErrorCode)
                    if entry.ConfigManagerErrorCode is not None
                    else None,
                    installed=installed,
                )
            )
        return devices

    def _lookup_installed_driver(self, conn, device_id: str) -> InstalledDriver | None:
        for signed in conn.Win32_PnPSignedDriver(DeviceID=device_id):
            driver_date = None
            if signed.DriverDate:
                # WMI datetime format: yyyymmddHHMMSS.mmmmmm+UUU
                driver_date = datetime.strptime(signed.DriverDate[:8], "%Y%m%d").date()
            return InstalledDriver(
                version=signed.DriverVersion or "unknown",
                driver_date=driver_date,
                publisher=signed.DriverProviderName or "unknown",
                signature_type=_SIGNATURE_MAP.get(bool(signed.IsSigned), SignatureType.UNSIGNED),
                inf_path=signed.InfName,
            )
        return None

    def export_driver_backup(self, device: Device, dest_dir: str) -> str:
        if not device.installed or not device.installed.inf_path:
            raise RuntimeError(f"No installed driver INF known for {device.instance_id}, cannot back up.")
        dest = f"{dest_dir}\\{device.instance_id.replace(chr(92), '_')}"
        subprocess.run(
            ["pnputil", "/export-driver", device.installed.inf_path, dest],
            check=True,
            capture_output=True,
        )
        return dest

    def install_driver(self, device: Device, package_path: str, *, dry_run: bool) -> InstallResult:
        command = f'pnputil /add-driver "{package_path}" /install'
        if dry_run:
            return InstallResult(success=True, commands_run=[command], message="dry-run: no changes made")
        result = subprocess.run(
            ["pnputil", "/add-driver", package_path, "/install"],
            capture_output=True,
            text=True,
            check=False,
        )
        return InstallResult(
            success=result.returncode == 0,
            commands_run=[command],
            message=result.stdout or result.stderr,
        )

    def create_restore_point(self, description: str) -> bool:
        # WMI SystemRestore.CreateRestorePoint(Description, EventType, RestorePointType)
        # EventType 100 = BEGIN_SYSTEM_CHANGE, RestorePointType 0 = APPLICATION_INSTALL.
        # Return value 0 indicates success — anything else must block the
        # install batch per docs/Architecture.md section 3.3.
        import wmi  # type: ignore

        conn = wmi.WMI(namespace="root\\default")
        try:
            result = conn.SystemRestore.CreateRestorePoint(description, 100, 0)
            return result == 0
        except Exception:  # noqa: BLE001 — WMI COM errors surface as bare Exception; any
            # failure here must fail closed (block the install batch), not propagate.
            return False
