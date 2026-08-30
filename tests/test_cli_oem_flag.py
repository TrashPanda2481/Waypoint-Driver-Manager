"""Tests for the CLI's opt-in `--oem` flag.

No real network access: the Dell OEM source's on-disk cache is pre-seeded
with the same real-data fixture `test_oem_sources.py` uses, so
`refresh(force=False)` finds the catalog "already downloaded" and just
parses it -- exercising the exact same code path `--oem` triggers in
production without spending a real ~57MB download on every test run.
"""

from __future__ import annotations

import json
import os
import shutil

from waypoint.cli.main import build_parser, main
from waypoint.core.models import Device
from waypoint.platform.mock import MockDeviceBackend

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures", "oem")

# Matches the Intel Integrated Sensor Solution Driver component in
# dell_catalog_sample.xml (see test_oem_sources.py for the same hwid).
_DELL_FIXTURE_HWID = "PCI\\VEN_8086&DEV_7745"


def _seed_dell_cache(cache_dir: str) -> None:
    """Pre-seed cache_dir/CatalogPC.xml so DellCatalogSource.refresh(force=False)
    treats the catalog as already downloaded and skips the network call.

    `build_oem_sources(cache_dir)` passes the *same* cache_dir straight to
    `DellCatalogSource(cache_dir)`, which stores `CatalogPC.xml` directly
    under it (see dell_catalog.py: `self._catalog_xml_path = self.cache_dir
    / "CatalogPC.xml"`) -- the same directory `LocalCacheSource` uses for
    `manifest.json`/`blobs/`. No collision (different filenames), but it
    does mean both sources currently share one `--cache-dir` root.
    """
    os.makedirs(cache_dir, exist_ok=True)
    shutil.copy(
        os.path.join(FIXTURES, "dell_catalog_sample.xml"),
        os.path.join(cache_dir, "CatalogPC.xml"),
    )


def test_oem_flag_defaults_to_off():
    parser = build_parser()
    args = parser.parse_args(["scan"])
    assert args.oem is False


def test_oem_flag_parses_before_subcommand():
    parser = build_parser()
    args = parser.parse_args(["--oem", "scan", "--json"])
    assert args.oem is True
    assert args.command == "scan"


def test_scan_without_oem_does_not_match_dell_only_hwid(tmp_path, monkeypatch, capsys):
    """Sanity check: without --oem, a device whose only driver source is the
    Dell fixture catalog should NOT get matched (proves the OEM source
    genuinely isn't in play by default, not just that it's a no-op)."""
    cache_dir = tmp_path / "cache"
    _seed_dell_cache(str(cache_dir))
    audit_log = tmp_path / "audit.jsonl"

    device = Device(
        hwids=(_DELL_FIXTURE_HWID,),
        class_guid="{class}",
        class_name="Sensor",
        friendly_name="Fake Intel Sensor",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    monkeypatch.setattr(
        "waypoint.engine.factory.build_default_backend",
        lambda: MockDeviceBackend([device]),
    )

    rc = main(
        [
            "--cache-dir",
            str(cache_dir),
            "--audit-log",
            str(audit_log),
            "scan",
            "--json",
        ]
    )
    out = json.loads(capsys.readouterr().out)
    assert rc == 1  # missing driver, no source has it
    assert out[0]["status"] == "missing"
    assert out[0]["candidate_count"] == 0


def test_scan_with_oem_flag_wires_dell_source(tmp_path, monkeypatch, capsys):
    cache_dir = tmp_path / "cache"
    _seed_dell_cache(str(cache_dir))
    audit_log = tmp_path / "audit.jsonl"

    device = Device(
        hwids=(_DELL_FIXTURE_HWID,),
        class_guid="{class}",
        class_name="Sensor",
        friendly_name="Fake Intel Sensor",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    monkeypatch.setattr(
        "waypoint.engine.factory.build_default_backend",
        lambda: MockDeviceBackend([device]),
    )

    rc = main(
        [
            "--cache-dir",
            str(cache_dir),
            "--audit-log",
            str(audit_log),
            "--oem",
            # Dell's assumed signature tier is UNSIGNED (see
            # dell_catalog.py's _ASSUMED_SIGNATURE); the default
            # min-signature policy (attestation) would filter it back out
            # before it ever reached the JSON output, so this test has to
            # loosen the policy explicitly to prove the *source* wiring
            # works, independently of the (correct, conservative) default
            # signature gate.
            "--min-signature",
            "unsigned",
            "scan",
            "--json",
        ]
    )
    out = json.loads(capsys.readouterr().out)
    assert rc == 1  # still "missing" per Device Manager problem code 28,
    # but a real candidate was found this time
    assert out[0]["candidate_count"] == 1


def test_oem_cache_dir_is_reused_not_redownloaded(tmp_path, monkeypatch):
    """Confirms the cost-control property claimed in the --oem help text:
    a pre-populated cache_dir is reused via refresh(force=False), not
    re-downloaded, by asserting no network call is attempted."""
    from waypoint.sources.oem import dell_catalog as dell_catalog_module

    cache_dir = tmp_path / "cache"
    _seed_dell_cache(str(cache_dir))
    audit_log = tmp_path / "audit.jsonl"

    def _fail_if_called(url, dest):
        raise AssertionError(
            "download_file() should not be called when the catalog is "
            "already cached on disk (force=False)"
        )

    monkeypatch.setattr(dell_catalog_module, "download_file", _fail_if_called)
    monkeypatch.setattr(
        "waypoint.engine.factory.build_default_backend",
        lambda: MockDeviceBackend([]),
    )

    rc = main(
        [
            "--cache-dir",
            str(cache_dir),
            "--audit-log",
            str(audit_log),
            "--oem",
            "scan",
            "--json",
        ]
    )
    assert rc == 0  # no devices at all -> clean


def test_force_oem_refresh_flag_defaults_to_off():
    parser = build_parser()
    args = parser.parse_args(["scan"])
    assert args.force_oem_refresh is False


def test_force_oem_refresh_without_oem_warns_but_does_not_error(tmp_path, monkeypatch, capsys):
    """--force-oem-refresh without --oem should not silently pretend to
    do something -- it must warn on stderr, per the honesty instruction
    (don't let a flag look like it did something when it didn't)."""
    cache_dir = tmp_path / "cache"
    audit_log = tmp_path / "audit.jsonl"
    monkeypatch.setattr(
        "waypoint.engine.factory.build_default_backend",
        lambda: MockDeviceBackend([]),
    )

    rc = main(
        [
            "--cache-dir",
            str(cache_dir),
            "--audit-log",
            str(audit_log),
            "--force-oem-refresh",
            "scan",
            "--json",
        ]
    )
    captured = capsys.readouterr()
    assert rc == 0
    assert "no effect without --oem" in captured.err


def test_force_oem_refresh_forces_redownload_even_when_cached(tmp_path, monkeypatch):
    """The core behavior: with --oem --force-oem-refresh, refresh(force=True)
    must be called even though a valid cached catalog already exists --
    proven here by asserting download_file() IS called (the opposite of
    test_oem_cache_dir_is_reused_not_redownloaded)."""
    from waypoint.sources.oem import dell_catalog as dell_catalog_module

    cache_dir = tmp_path / "cache"
    _seed_dell_cache(str(cache_dir))
    audit_log = tmp_path / "audit.jsonl"

    call_count = {"n": 0}
    real_extract_needed_file = os.path.join(FIXTURES, "dell_catalog_sample.xml")

    def _fake_download(url, dest):
        call_count["n"] += 1
        # Simulate a real download landing at `dest` (a .cab path inside a
        # tempdir) by dropping in a minimal real cab-less stand-in: since
        # DellCatalogSource.refresh() expects to extract a .cab, we instead
        # monkeypatch extract_cab too, to keep this a pure "was a network
        # call attempted" check without needing a real .cab fixture.
        with open(dest, "wb") as f:
            f.write(b"fake-cab-bytes")

    def _fake_extract_cab(cab_path, dest_dir):
        # Return the real fixture XML path as if it were extracted --
        # keeps the rest of refresh()'s parse step working on real data.
        import shutil
        from pathlib import Path

        target = Path(dest_dir) / "CatalogPC.xml"
        shutil.copy(real_extract_needed_file, target)
        return [target]

    monkeypatch.setattr(dell_catalog_module, "download_file", _fake_download)
    monkeypatch.setattr(dell_catalog_module, "extract_cab", _fake_extract_cab)
    monkeypatch.setattr(
        "waypoint.engine.factory.build_default_backend",
        lambda: MockDeviceBackend([]),
    )

    rc = main(
        [
            "--cache-dir",
            str(cache_dir),
            "--audit-log",
            str(audit_log),
            "--oem",
            "--force-oem-refresh",
            "scan",
            "--json",
        ]
    )
    assert rc == 0
    assert call_count["n"] == 1  # download_file WAS called despite the pre-seeded cache
