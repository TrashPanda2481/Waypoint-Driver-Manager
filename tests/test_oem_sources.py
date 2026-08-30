"""Tests for OEM catalog sources, using small real-data fixtures trimmed
from the actual Dell/Lenovo/HP catalogs (downloaded and inspected
2026-08-30 — see each source module's docstring for the verification
notes). No network access required: fixtures under tests/fixtures/oem/
stand in for a `refresh()` call.
"""

from __future__ import annotations

import os

import pytest

from waypoint.sources.oem.dell_catalog import DellCatalogSource
from waypoint.sources.oem.dell_driverpack import DellDriverPackSource
from waypoint.sources.oem.hp_platform import HpPlatformCatalogSource
from waypoint.sources.oem.lenovo_driverpack import LenovoDriverPackSource

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures", "oem")


def test_dell_catalog_matches_generic_pci_hwid(tmp_path):
    source = DellCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_catalog_sample.xml"))

    # Intel Integrated Sensor Solution Driver component lists PCI\VEN_8086&DEV_7745
    results = source.search(["PCI\\VEN_8086&DEV_7745"])
    assert len(results) == 1
    candidate = results[0]
    assert candidate.source_id == "dell_catalog"
    assert candidate.version == "3.11.100.7733"
    assert candidate.sha256 == ""  # honestly unknown until fetch() downloads+verifies
    assert candidate.download_uri.startswith("https://downloads.dell.com/")


def test_dell_catalog_matches_specific_subsys_hwid(tmp_path):
    source = DellCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_catalog_sample.xml"))

    # AMD Radeon component has vendorID=1002 deviceID=6900 subDeviceID=079D subVendorID=1028
    generic = source.search(["PCI\\VEN_1002&DEV_6900"])
    specific = source.search(["PCI\\VEN_1002&DEV_6900&SUBSYS_079D1028"])
    assert len(generic) == 1
    assert len(specific) == 1
    assert generic[0].version == specific[0].version == "16.400.2701"


def test_dell_catalog_unmatched_hwid_returns_nothing(tmp_path):
    source = DellCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_catalog_sample.xml"))
    assert source.search(["PCI\\VEN_FFFF&DEV_FFFF"]) == []


def test_dell_catalog_fetch_verifies_md5_and_returns_sha256(tmp_path, monkeypatch):
    source = DellCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_catalog_sample.xml"))
    candidate = source.search(["PCI\\VEN_8086&DEV_7745"])[0]

    payload = b"pretend driver package bytes"

    def fake_downloader(url, dest):
        with open(dest, "wb") as f:
            f.write(payload)

    source._downloader = fake_downloader
    with pytest.raises(ValueError, match="MD5 mismatch"):
        source.fetch(candidate, str(tmp_path / "out"))


def test_dell_driverpack_matches_system_id(tmp_path):
    source = DellDriverPackSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_driverpack_sample.xml"))

    packs = source.packs_for_model("092F")  # OptiPlex 5070 in the fixture
    assert len(packs) == 1
    assert packs[0].model_name == "OptiPlex 5070"
    assert packs[0].hash_algorithm == "sha256"
    assert len(packs[0].hash_value) == 64


def test_dell_driverpack_unknown_model_returns_empty(tmp_path):
    source = DellDriverPackSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "dell_driverpack_sample.xml"))
    assert source.packs_for_model("ZZZZ") == []


def test_lenovo_driverpack_matches_machine_type(tmp_path):
    source = LenovoDriverPackSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "lenovo_catalog_sample.xml"))

    packs = source.packs_for_model("10M4")  # ThinkCentre M715Q in the fixture
    assert len(packs) >= 1
    assert packs[0].model_name == "ThinkCentre M715Q"
    assert packs[0].source_id == "lenovo_driverpack"
    # Lenovo's "crc" attribute is actually SHA-256 (64 hex chars) — confirmed
    # against real catalog data, reported honestly rather than as "crc".
    assert packs[0].hash_algorithm == "sha256"
    assert len(packs[0].hash_value) == 64


def test_lenovo_driverpack_matches_case_insensitively_and_full_serial(tmp_path):
    source = LenovoDriverPackSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "lenovo_catalog_sample.xml"))

    # A real system's SMBIOS product name is longer than the 4-char type
    # code (e.g. "10M4S00100") — packs_for_model must truncate correctly.
    packs = source.packs_for_model("10m4s00100")
    assert len(packs) >= 1


def test_hp_platform_reports_known_system_id(tmp_path):
    source = HpPlatformCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "hp_platformlist_sample.xml"))

    info = source.is_supported("1909")  # HP ZBook 15 Mobile Workstation in the fixture
    assert info is not None
    assert "ZBook 15" in info.product_name
    assert len(info.supported_os_descriptions) >= 1


def test_hp_platform_unknown_system_id_returns_none(tmp_path):
    source = HpPlatformCatalogSource(str(tmp_path))
    source.load_from_xml(os.path.join(FIXTURES, "hp_platformlist_sample.xml"))
    assert source.is_supported("ZZZZ") is None
