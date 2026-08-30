"""Lenovo's per-model driver pack catalog.

https://download.lenovo.com/cdrt/td/catalogv2.xml — verified by
downloading and inspecting the real file (2026-08-30). Each `<Model>`
lists one or more 4-character `<Types>/<Type>` machine-type codes (the
same code that prefixes a real Lenovo `Win32_ComputerSystem.Model` /
SMBIOS system-product-name string, e.g. "20U9..." for a ThinkPad),
alongside `<SCCM>` elements — full driver-pack bundles per OS/version —
and `<BIOS>` elements for firmware.

Only `<SCCM>` (driver pack) entries are exposed through `packs_for_model`.
`<BIOS>` entries are intentionally excluded: firmware flashing is a
categorically higher-risk operation than installing a device driver (a
bad BIOS flash can brick a machine in a way no System Restore / driver
rollback fixes), and Waypoint doesn't offer it through the same
lower-friction path used for drivers until there's an explicit, separately
confirmed firmware workflow — consistent with the mandatory-restore-point/
per-driver-backup safety posture in docs/Architecture.md section 3.3.

Vendor-naming quirk worth flagging rather than silently trusting: the
`crc` attribute Lenovo publishes on each `<SCCM>`/`<BIOS>` element is a
64-hex-character digest — i.e. actually a SHA-256 hash despite the
attribute name, not a CRC32. Confirmed by length (64 hex chars = 32 bytes)
against real catalog data. `DriverPack.hash_algorithm` reports this
correctly as `"sha256"`, not `"crc"`.
"""

from __future__ import annotations

import xml.etree.ElementTree as ET
from collections.abc import Callable
from datetime import date, datetime
from pathlib import Path

from waypoint.sources.oem.http import download_file
from waypoint.sources.oem.model_pack import DriverPack

DEFAULT_CATALOG_URL = "https://download.lenovo.com/cdrt/td/catalogv2.xml"


class LenovoDriverPackSource:
    source_id = "lenovo_driverpack"

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
        self._catalog_xml_path = self.cache_dir / "catalogv2.xml"
        self._index: dict[str, list[DriverPack]] | None = None

    def refresh(self, *, force: bool = False) -> None:
        """Lenovo publishes this catalog as plain XML, not a .cab — no
        extraction step needed, just a direct download."""
        if force or not self._catalog_xml_path.exists():
            self._downloader(self._catalog_url, str(self._catalog_xml_path))
        self._index = None
        self.load_from_xml(str(self._catalog_xml_path))

    def load_from_xml(self, xml_path: str) -> None:
        tree = ET.parse(xml_path)
        root = tree.getroot()

        index: dict[str, list[DriverPack]] = {}
        for model in root.findall("Model"):
            model_name = model.attrib.get("name", "unknown")
            types = [t.text.strip() for t in model.findall("Types/Type") if t.text]
            if not types:
                continue

            for sccm in model.findall("SCCM"):
                url = (sccm.text or "").strip()
                if not url:
                    continue
                crc = sccm.attrib.get("crc", "")
                md5 = sccm.attrib.get("md5", "")
                hash_algo, hash_value = ("sha256", crc) if len(crc) == 64 else ("md5", md5)
                pack = DriverPack(
                    pack_id=url.rsplit("/", 1)[-1],
                    model_name=model_name,
                    model_key="",  # filled in per-type below
                    os_label=sccm.attrib.get("os", "unknown"),
                    version=sccm.attrib.get("version", "unknown"),
                    release_date=_parse_date(sccm.attrib.get("date", "")),
                    url=url,
                    hash_algorithm=hash_algo,
                    hash_value=hash_value,
                    size_bytes=0,  # not published by this catalog
                    source_id=self.source_id,
                )
                for type_code in types:
                    key = type_code.upper()
                    index.setdefault(key, []).append(
                        DriverPack(**{**pack.__dict__, "model_key": key})
                    )

        self._index = index

    def packs_for_model(self, model_key: str) -> list[DriverPack]:
        """`model_key` is the 4-character Lenovo machine-type code — the
        first 4 characters of the real system's SMBIOS product name."""
        if self._index is None:
            self.load_from_xml(str(self._catalog_xml_path))
        lookup_key = model_key.strip().upper()[:4]
        return list(self._index.get(lookup_key, []))  # type: ignore[union-attr]


def _parse_date(value: str) -> date | None:
    if not value:
        return None
    try:
        return datetime.strptime(value, "%Y-%m-%d").date()  # noqa: DTZ007 -- date-only field, no TZ published
    except ValueError:
        return None
