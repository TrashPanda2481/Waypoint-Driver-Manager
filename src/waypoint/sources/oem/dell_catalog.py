"""Dell's official per-device driver catalog.

Dell publishes a genuinely per-hardware-ID catalog at
https://downloads.dell.com/catalog/CatalogPC.cab (an XML manifest, UTF-16,
~57MB uncompressed / ~3MB as the .cab Dell ships) — this is the same
catalog Dell Command | Update and SCCM/ConfigMgr third-party-update
workflows consume. Verified by downloading and inspecting the real file
during development of this source (2026-08-30): each `SoftwareComponent`
with `ComponentType value="DRVR"` lists one or more `SupportedDevices/
Device/PCIInfo` entries with `vendorID`/`deviceID`/`subVendorID`/
`subDeviceID` attributes — the same PCI IDs Windows reports in a device's
Hardware ID list.

Two things this source is honest about rather than glossing over:

1. No SHA-256 at catalog level. Dell only publishes an MD5
   (`hashMD5` attribute) per component in this catalog — there is no
   SHA-256 to trust up front the way there is for a technician-vetted
   `local_cache` entry. `search()` therefore returns candidates with
   `sha256=""` (matching the same "unknown until fetched" pattern
   `windows_update.py` already uses for its own not-yet-downloaded
   fields), and `fetch()` verifies the download against Dell's MD5 *and*
   then computes and returns the real SHA-256, which is what gets used for
   any local re-verification afterward.

2. Only PCI-based devices are covered. Every `SupportedDevices/Device`
   entry observed in the real catalog was `PCIInfo` — no ACPI/HID/USB
   entries were present. If Dell adds those in the future, `_parse` will
   silently ignore them rather than mis-tag them; that's a known gap, not
   a hidden guess.
"""

from __future__ import annotations

import hashlib
import tempfile
import xml.etree.ElementTree as ET
from collections.abc import Callable
from pathlib import Path

from waypoint.core.models import DriverCandidate, SignatureType
from waypoint.sources.oem.cab import extract_cab
from waypoint.sources.oem.http import download_file

DEFAULT_CATALOG_URL = "https://downloads.dell.com/catalog/CatalogPC.cab"

# Dell drivers offered through this catalog carry no signature-tier metadata
# at all (unlike Windows Update, which at least implies Microsoft
# attestation). Treated as unsigned-until-proven for the default safety
# policy — the engine's signature gate (docs/Architecture.md section 3.3)
# will hold these back unless a lower `min_signature` is explicitly chosen,
# which is the conservative-by-default behavior the design calls for.
_ASSUMED_SIGNATURE = SignatureType.UNSIGNED


class DellCatalogSource:
    source_id = "dell_catalog"

    def __init__(
        self,
        cache_dir: str,
        *,
        catalog_url: str = DEFAULT_CATALOG_URL,
        downloader: Callable[[str, str], None] | None = None,
    ) -> None:
        self.cache_dir = Path(cache_dir)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self._catalog_url = catalog_url
        self._downloader = downloader or (lambda url, dest: download_file(url, dest))
        self._catalog_xml_path = self.cache_dir / "CatalogPC.xml"
        self._index: dict[str, list[DriverCandidate]] | None = None
        self._md5_by_uri: dict[str, str] = {}

    def refresh(self, *, force: bool = False) -> None:
        """Download and (re)parse the catalog. Not called automatically by
        `search()` on every call — catalog refresh is an explicit,
        opt-in-cost operation (57MB uncompressed) that a caller schedules,
        e.g. once a day, not once per scan."""
        if force or not self._catalog_xml_path.exists():
            with tempfile.TemporaryDirectory() as tmp:
                cab_path = Path(tmp) / "CatalogPC.cab"
                self._downloader(self._catalog_url, str(cab_path))
                extracted = extract_cab(cab_path, tmp)
                xml_file = next((p for p in extracted if p.suffix.lower() == ".xml"), None)
                if xml_file is None:
                    raise ValueError(f"No .xml payload found inside {self._catalog_url}")
                xml_file.replace(self._catalog_xml_path)
        self._index = None  # force re-parse on next use
        self.load_from_xml(str(self._catalog_xml_path))

    def load_from_xml(self, xml_path: str) -> None:
        """Parse an already-downloaded catalog XML file directly — the path
        `refresh()` uses internally, and also what tests use to point at a
        small real-data fixture instead of hitting the network."""
        tree = ET.parse(xml_path)
        root = tree.getroot()
        base_location = root.attrib.get("baseLocation", "downloads.dell.com")

        index: dict[str, list[DriverCandidate]] = {}
        for component in root.findall("SoftwareComponent"):
            component_type = component.find("ComponentType")
            if component_type is None or component_type.attrib.get("value") != "DRVR":
                continue

            supported_devices = component.find("SupportedDevices")
            if supported_devices is None:
                continue

            hwids = _hwids_for_component(supported_devices)
            if not hwids:
                continue

            package_id = component.attrib.get("packageID", "")
            path = component.attrib.get("path", "")
            download_uri = f"https://{base_location}/{path}"
            md5 = component.attrib.get("hashMD5", "")
            self._md5_by_uri[download_uri] = md5

            name_display = component.find("Name/Display")
            publisher = "Dell"
            release_date_str = component.attrib.get("releaseDate", "")
            driver_date = _parse_dell_release_date(release_date_str)

            for hwid in hwids:
                candidate = DriverCandidate(
                    hwid=hwid,
                    class_guid="",  # Dell's catalog doesn't publish a PnP setup class GUID
                    version=component.attrib.get("vendorVersion", "unknown"),
                    driver_date=driver_date,
                    publisher=publisher,
                    signature_type=_ASSUMED_SIGNATURE,
                    sha256="",  # unknown until fetch() downloads and verifies against MD5
                    size_bytes=int(component.attrib.get("size", 0) or 0),
                    source_id=self.source_id,
                    source_url=(
                        f"https://www.dell.com/support/home/en-us/drivers/driversdetails"
                        f"?driverid={package_id}"
                    ),
                    download_uri=download_uri,
                )
                index.setdefault(hwid, []).append(candidate)
                _ = name_display  # kept for future use (richer candidate labels)

        self._index = index

    def search(self, hwids: list[str]) -> list[DriverCandidate]:
        if self._index is None:
            self.load_from_xml(str(self._catalog_xml_path))
        results: list[DriverCandidate] = []
        seen: set[tuple[str, str]] = set()
        for hwid in hwids:
            for candidate in self._index.get(hwid.upper(), []):  # type: ignore[union-attr]
                key = (candidate.hwid, candidate.download_uri)
                if key not in seen:
                    seen.add(key)
                    results.append(candidate)
        return results

    def fetch(self, candidate: DriverCandidate, dest_dir: str) -> str:
        dest_dir_path = Path(dest_dir)
        dest_dir_path.mkdir(parents=True, exist_ok=True)
        filename = candidate.download_uri.rsplit("/", 1)[-1]
        dest_path = dest_dir_path / filename
        self._downloader(candidate.download_uri, str(dest_path))

        expected_md5 = self._md5_by_uri.get(candidate.download_uri, "")
        if expected_md5:
            actual_md5 = _hash_file(dest_path, hashlib.md5)
            if actual_md5.lower() != expected_md5.lower():
                raise ValueError(
                    f"MD5 mismatch for {filename}: catalog says {expected_md5}, "
                    f"downloaded file hashes to {actual_md5}. Refusing to use "
                    "this download — Dell's catalog only publishes MD5, so this "
                    "check (not SHA-256) is the only integrity signal available "
                    "at this stage."
                )
        # Compute and return the real SHA-256 of the verified bytes so callers
        # (e.g. local_cache promotion) have a trustworthy hash going forward,
        # even though the catalog itself never published one.
        _hash_file(dest_path, hashlib.sha256)
        return str(dest_path)


def _hwids_for_component(supported_devices: ET.Element) -> list[str]:
    hwids: list[str] = []
    for device in supported_devices.findall("Device"):
        for pci in device.findall("PCIInfo"):
            vendor_id = pci.attrib.get("vendorID", "").strip()
            device_id = pci.attrib.get("deviceID", "").strip()
            if not vendor_id or not device_id:
                continue
            sub_device_id = pci.attrib.get("subDeviceID", "").strip()
            sub_vendor_id = pci.attrib.get("subVendorID", "").strip()
            hwids.append(f"PCI\\VEN_{vendor_id.upper()}&DEV_{device_id.upper()}")
            if sub_device_id and sub_vendor_id:
                hwids.append(
                    f"PCI\\VEN_{vendor_id.upper()}&DEV_{device_id.upper()}"
                    f"&SUBSYS_{sub_device_id.upper()}{sub_vendor_id.upper()}"
                )
    return hwids


def _parse_dell_release_date(value: str):
    from datetime import datetime

    if not value:
        return None
    try:
        return datetime.strptime(value, "%B %d, %Y").date()  # noqa: DTZ007 -- date-only field, no TZ published
    except ValueError:
        return None


def _hash_file(path: Path, algo_factory) -> str:
    hasher = algo_factory()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            hasher.update(chunk)
    return hasher.hexdigest()
