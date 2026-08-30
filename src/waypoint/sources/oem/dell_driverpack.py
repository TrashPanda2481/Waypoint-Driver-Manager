"""Dell's per-model driver pack catalog.

https://downloads.dell.com/catalog/DriverPackCatalog.cab — verified by
downloading and inspecting the real file (2026-08-30). Each
`DriverPackage` lists `SupportedSystems/Brand/Model` entries with a
`systemID` attribute (Dell's SMBIOS-reported system ID, the same value
Dell Command | Update and Windows' own `Win32_ComputerSystemProduct.
IdentifyingNumber`-adjacent SMBIOS fields expose) and, unlike the
per-device `CatalogPC.cab`, a real `Cryptography/Hash algorithm="SHA256"`
per package — so `DriverPack.hash_algorithm` here is honestly `"sha256"`,
not a fallback.
"""

from __future__ import annotations

import xml.etree.ElementTree as ET
from collections.abc import Callable
from datetime import date
from pathlib import Path

from waypoint.sources.oem.cab import extract_cab
from waypoint.sources.oem.http import download_file
from waypoint.sources.oem.model_pack import DriverPack

DEFAULT_CATALOG_URL = "https://downloads.dell.com/catalog/DriverPackCatalog.cab"


class DellDriverPackSource:
    source_id = "dell_driverpack"

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
        self._catalog_xml_path = self.cache_dir / "DriverPackCatalog.xml"
        self._index: dict[str, list[DriverPack]] | None = None

    def refresh(self, *, force: bool = False) -> None:
        if force or not self._catalog_xml_path.exists():
            import tempfile

            with tempfile.TemporaryDirectory() as tmp:
                cab_path = Path(tmp) / "DriverPackCatalog.cab"
                self._downloader(self._catalog_url, str(cab_path))
                extracted = extract_cab(cab_path, tmp)
                xml_file = next((p for p in extracted if p.suffix.lower() == ".xml"), None)
                if xml_file is None:
                    raise ValueError(f"No .xml payload found inside {self._catalog_url}")
                xml_file.replace(self._catalog_xml_path)
        self._index = None
        self.load_from_xml(str(self._catalog_xml_path))

    def load_from_xml(self, xml_path: str) -> None:
        # DriverPackCatalog.xml declares a default namespace
        # (xmlns="openmanage/cm/dm") on its root element, unlike CatalogPC.xml
        # (no default namespace) — verified against the real downloaded file.
        # The `{*}tag` wildcard matches the tag regardless of namespace so
        # this parses correctly either way.
        tree = ET.parse(xml_path)
        root = tree.getroot()
        base_location = root.attrib.get("baseLocation", "downloads.dell.com")

        index: dict[str, list[DriverPack]] = {}
        for package in root.findall("{*}DriverPackage"):
            models = package.findall("{*}SupportedSystems/{*}Brand/{*}Model")
            if not models:
                continue

            os_names = [
                (os_el.find("{*}Display").text or "").strip()
                for os_el in package.findall("{*}SupportedOperatingSystems/{*}OperatingSystem")
                if os_el.find("{*}Display") is not None
            ]
            os_label = ", ".join(n for n in os_names if n) or "unknown"

            hash_algo, hash_value = _best_hash(package)
            path = package.attrib.get("path", "")
            url = f"https://{base_location}/{path}"
            release_dt = _parse_dell_release_date(package.attrib.get("dateTime", ""))

            for model in models:
                system_id = model.attrib.get("systemID", "")
                if not system_id:
                    continue
                pack = DriverPack(
                    pack_id=package.attrib.get("releaseID", ""),
                    model_name=model.attrib.get("name", "unknown"),
                    model_key=system_id,
                    os_label=os_label,
                    version=package.attrib.get("dellVersion", "unknown"),
                    release_date=release_dt,
                    url=url,
                    hash_algorithm=hash_algo,
                    hash_value=hash_value,
                    size_bytes=int(package.attrib.get("size", 0) or 0),
                    source_id=self.source_id,
                )
                index.setdefault(system_id.upper(), []).append(pack)

        self._index = index

    def packs_for_model(self, model_key: str) -> list[DriverPack]:
        if self._index is None:
            self.load_from_xml(str(self._catalog_xml_path))
        return list(self._index.get(model_key.upper(), []))  # type: ignore[union-attr]


def _best_hash(package: ET.Element) -> tuple[str, str]:
    """Prefer SHA256, fall back to SHA1, then MD5 — whatever the catalog
    actually published for this specific package (older packages in this
    catalog sometimes only carry weaker hashes)."""
    crypto = package.find("{*}Cryptography")
    if crypto is None:
        return "md5", package.attrib.get("hashMD5", "")
    hashes = {h.attrib.get("algorithm", "").upper(): (h.text or "").strip() for h in crypto.findall("{*}Hash")}
    for algo in ("SHA256", "SHA1", "MD5"):
        if hashes.get(algo):
            return algo.lower(), hashes[algo]
    return "md5", package.attrib.get("hashMD5", "")


def _parse_dell_release_date(value: str) -> date | None:
    from datetime import datetime

    if not value:
        return None
    for fmt in ("%Y-%m-%dT%H:%M:%S", "%Y-%m-%d"):
        try:
            return datetime.strptime(value[:19], fmt).date()  # noqa: DTZ007 -- date-only field, no TZ published
        except ValueError:
            continue
    return None
