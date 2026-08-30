"""HP platform support lookup — deliberately NOT a driver source.

HP's public update feed (`HpCatalogForSms.latest.cab`,
https://hpia.hpcloud.hp.com/downloads/sccmcatalog/HpCatalogForSms.latest.cab)
is a WSUS Software Distribution Package (SDP) feed. Verified by downloading
and inspecting the real file (2026-08-30): applicability for each update is
expressed as `bar:WmiQuery` elements containing arbitrary WQL, e.g.

    select * from Win32_ComputerSystem
    where (Manufacturer='Hewlett-Packard' and not (Model like '%Proliant%'))
       or (Manufacturer='HP')

combined with further WMI queries against `Win32_BaseBoard` and others via
`lar:And`/`lar:Or` logical-rule XML. There is no flat, declarative
hardware-ID or model-ID list to parse the way there is for Dell or Lenovo —
correctly resolving "does update X apply to this machine" means evaluating
arbitrary WQL against live system state, which is a WMI query interpreter,
not a catalog parser.

Building and trusting that interpreter is out of scope for this milestone.
Rather than fake per-device matching on top of a format that doesn't
support it (which would silently violate the "do not present guessed
results as fact" principle this project holds itself to), this module only
implements the one thing HP *does* publish in a clean, declarative form:
the platform/support list at
https://hpia.hpcloud.hp.com/ref/platformList.cab (the same list HP Image
Assistant itself uses to look up per-platform reference bundles by
`SystemID` — HP's equivalent of Dell's/Lenovo's system-model identifier,
read from `Win32_BaseBoard.Product` on real HP hardware).

`HpPlatformCatalogSource.is_supported(system_id)` answers "is this exact
HP model in HP's supported-platform list, and for which OS versions" —
useful for the GUI to say "your model is covered by HP, driver-pack
sourcing not yet implemented" instead of silently doing nothing. It is not
a `DriverSource` or a `ModelDriverPackSource`, and callers must not treat
it as one.
"""

from __future__ import annotations

import xml.etree.ElementTree as ET
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from waypoint.sources.oem.cab import extract_cab
from waypoint.sources.oem.http import download_file

DEFAULT_PLATFORM_LIST_URL = "https://hpia.hpcloud.hp.com/ref/platformList.cab"


@dataclass(frozen=True)
class HpPlatformInfo:
    system_id: str
    product_name: str
    supported_os_descriptions: tuple[str, ...]


class HpPlatformCatalogSource:
    """Not a DriverSource / ModelDriverPackSource — see module docstring."""

    source_id = "hp_platform"

    def __init__(
        self,
        cache_dir: str,
        *,
        catalog_url: str = DEFAULT_PLATFORM_LIST_URL,
        downloader: Callable[[str, str], None] | None = None,
    ) -> None:
        self.cache_dir = Path(cache_dir)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self._catalog_url = catalog_url
        self._downloader = downloader or (lambda url, dest: download_file(url, dest))
        self._catalog_xml_path = self.cache_dir / "platformList.xml"
        self._index: dict[str, HpPlatformInfo] | None = None

    def refresh(self, *, force: bool = False) -> None:
        if force or not self._catalog_xml_path.exists():
            import tempfile

            with tempfile.TemporaryDirectory() as tmp:
                cab_path = Path(tmp) / "platformList.cab"
                self._downloader(self._catalog_url, str(cab_path))
                extracted = extract_cab(cab_path, tmp)
                xml_file = next((p for p in extracted if p.suffix.lower() == ".xml"), None)
                if xml_file is None:
                    raise ValueError(f"No .xml payload found inside {self._catalog_url}")
                xml_file.replace(self._catalog_xml_path)
        self._index = None
        self.load_from_xml(str(self._catalog_xml_path))

    def load_from_xml(self, xml_path: str) -> None:
        tree = ET.parse(xml_path)
        root = tree.getroot()
        index: dict[str, HpPlatformInfo] = {}
        for platform in root.findall("Platform"):
            system_id_el = platform.find("SystemID")
            product_name_el = platform.find("ProductName")
            if system_id_el is None or system_id_el.text is None:
                continue
            system_id = system_id_el.text.strip().upper()
            os_descriptions = tuple(
                (os_el.find("OSDescription").text or "").strip()
                for os_el in platform.findall("OS")
                if os_el.find("OSDescription") is not None and os_el.find("OSDescription").text
            )
            index[system_id] = HpPlatformInfo(
                system_id=system_id,
                product_name=(product_name_el.text or "unknown").strip() if product_name_el is not None else "unknown",
                supported_os_descriptions=tuple(sorted(set(os_descriptions))),
            )
        self._index = index

    def is_supported(self, system_id: str) -> HpPlatformInfo | None:
        """Returns platform info if HP lists this SystemID as supported,
        else None. This confirms the platform is *known to HP* — it does
        not, and cannot from this data alone, tell you which drivers apply
        to a specific device on that platform. See module docstring."""
        if self._index is None:
            self.load_from_xml(str(self._catalog_xml_path))
        return self._index.get(system_id.strip().upper())  # type: ignore[union-attr]
