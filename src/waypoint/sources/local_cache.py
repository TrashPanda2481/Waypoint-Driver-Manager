"""Local, content-addressed driver cache.

This is the one source that runs fully today (no network dependency), and
it's the direct fix for SDI's "one monolithic 20-60GB blob" model: a
technician adds drivers here incrementally, one at a time, keyed by their
SHA-256 hash rather than by filename/folder convention. Re-scans across
many machines dedupe automatically because the key *is* the content hash.

Layout on disk:
    <cache_root>/
      manifest.json          # list of DriverCandidate records (as dicts)
      blobs/<sha256>/        # the actual driver package contents
"""

from __future__ import annotations

import hashlib
import json
import shutil
from dataclasses import asdict
from datetime import date
from pathlib import Path

from waypoint.core.models import DriverCandidate, SignatureType


class LocalCacheSource:
    source_id = "local_cache"

    def __init__(self, cache_root: str) -> None:
        self.cache_root = Path(cache_root)
        self.cache_root.mkdir(parents=True, exist_ok=True)
        (self.cache_root / "blobs").mkdir(exist_ok=True)
        self._manifest_path = self.cache_root / "manifest.json"
        if not self._manifest_path.exists():
            self._manifest_path.write_text("[]")

    def _load_manifest(self) -> list[dict]:
        return json.loads(self._manifest_path.read_text())

    def _save_manifest(self, entries: list[dict]) -> None:
        self._manifest_path.write_text(json.dumps(entries, indent=2, default=str))

    def add_package(
        self,
        source_file: str,
        *,
        hwid: str,
        class_guid: str,
        version: str,
        driver_date: date | None,
        publisher: str,
        signature_type: SignatureType,
    ) -> DriverCandidate:
        """Register a driver package a technician has vetted, keyed by its
        content hash. This is the explicit, opt-in alternative to trusting
        an opaque third-party archive.
        """
        src_path = Path(source_file)
        sha256 = _hash_file(src_path)
        blob_dir = self.cache_root / "blobs" / sha256
        if not blob_dir.exists():
            blob_dir.mkdir(parents=True)
            shutil.copy2(src_path, blob_dir / src_path.name)

        candidate = DriverCandidate(
            hwid=hwid,
            class_guid=class_guid,
            version=version,
            driver_date=driver_date,
            publisher=publisher,
            signature_type=signature_type,
            sha256=sha256,
            size_bytes=src_path.stat().st_size,
            source_id=self.source_id,
            source_url=str(src_path),
            download_uri=str(blob_dir / src_path.name),
        )
        entries = self._load_manifest()
        entries.append(asdict(candidate))
        self._save_manifest(entries)
        return candidate

    def search(self, hwids: list[str]) -> list[DriverCandidate]:
        entries = self._load_manifest()
        hwid_set = set(hwids)
        results = []
        for entry in entries:
            if entry["hwid"] in hwid_set:
                entry = dict(entry)
                entry["signature_type"] = SignatureType(entry["signature_type"])
                if entry.get("driver_date"):
                    entry["driver_date"] = date.fromisoformat(entry["driver_date"])
                results.append(DriverCandidate(**entry))
        return results

    def fetch(self, candidate: DriverCandidate, dest_dir: str) -> str:
        src = Path(candidate.download_uri)
        if _hash_file(src) != candidate.sha256:
            raise ValueError(
                f"Hash mismatch for {src}: cache is corrupt or candidate metadata is stale."
            )
        dest_path = Path(dest_dir) / src.name
        Path(dest_dir).mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dest_path)
        return str(dest_path)


def _hash_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            hasher.update(chunk)
    return hasher.hexdigest()
