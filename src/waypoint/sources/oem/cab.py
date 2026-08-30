"""Portable Microsoft Cabinet (.cab) extraction.

OEM catalogs (Dell, HP, Lenovo's BIOS/driver packs) are distributed as .cab
files. Rather than bundle a Python CAB-parsing dependency, this shells out to
whichever extractor is actually available:

  - Windows: `expand.exe` ships with every Windows installation since
    Windows 2000 (https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/expand) —
    no extra install required on the platform Waypoint primarily targets.
  - Linux/macOS (dev/CI machines, not an install target): `cabextract`
    (https://www.cabextract.org.uk/), a common but *not universally
    preinstalled* package — this is a real, disclosed dependency for
    running the OEM-catalog code path outside Windows, not something
    Waypoint silently assumes.

If neither is found, `extract_cab` raises `CabExtractionUnavailable` with an
actionable message instead of failing in a confusing way deep inside catalog
parsing.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path


class CabExtractionUnavailable(RuntimeError):
    pass


def extract_cab(cab_path: str | Path, dest_dir: str | Path) -> list[Path]:
    """Extract every file in `cab_path` into `dest_dir`. Returns the list of
    extracted file paths. Raises `CabExtractionUnavailable` if no supported
    extractor is present, and `subprocess.CalledProcessError` if the
    extractor itself fails (e.g. a truncated download)."""
    cab_path = Path(cab_path)
    dest_dir = Path(dest_dir)
    dest_dir.mkdir(parents=True, exist_ok=True)

    if sys.platform == "win32":
        # expand.exe -F:* <cab> <destdir> extracts every file, preserving
        # names but not subdirectories (matches how Dell/HP/Lenovo cabs are
        # laid out: flat, single XML payload).
        subprocess.run(
            ["expand.exe", "-F:*", str(cab_path), str(dest_dir)],
            check=True,
            capture_output=True,
        )
    elif shutil.which("cabextract"):
        subprocess.run(
            ["cabextract", "-d", str(dest_dir), str(cab_path)],
            check=True,
            capture_output=True,
        )
    else:
        raise CabExtractionUnavailable(
            f"No CAB extractor available to unpack {cab_path}. On Windows this "
            "should never happen (expand.exe ships with the OS) — if you see "
            "this on Windows, something is very wrong with the PATH. On "
            "Linux/macOS dev machines, install cabextract, e.g. "
            "`apt install cabextract` / `brew install cabextract`."
        )

    return [p for p in dest_dir.iterdir() if p.is_file()]
