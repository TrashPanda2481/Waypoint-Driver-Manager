"""DriverSource plugin interface.

Every driver source — Windows Update Catalog, an OEM catalog, a local
technician-built cache — implements this one interface. The engine treats
all sources identically: it asks each enabled source for candidates by
hardware ID and merges the results before handing them to
`core.matching.assess_all`. Adding a new source never requires touching
core matching logic or the GUI.
"""

from __future__ import annotations

from typing import Protocol

from waypoint.core.models import DriverCandidate


class DriverSource(Protocol):
    source_id: str

    def search(self, hwids: list[str]) -> list[DriverCandidate]:
        """Return every candidate this source has for any of `hwids`.
        Must not download anything — search returns metadata only
        (version, signature, sha256, download_uri). Download happens later,
        only for candidates the user/script actually selects, and the
        downloaded bytes are hash-verified against `sha256` before use.
        """
        ...

    def fetch(self, candidate: DriverCandidate, dest_dir: str) -> str:
        """Download `candidate` into `dest_dir`, verify its SHA-256 against
        `candidate.sha256`, and return the local path. Must raise if the
        hash does not match — never install an unverified download.
        """
        ...
