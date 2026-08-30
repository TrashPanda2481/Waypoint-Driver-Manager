"""Per-model driver pack sourcing — a different shape from `DriverSource`.

`DriverSource` (sources/base.py) answers "what drivers exist for this
hardware ID?" Some real OEM catalogs don't publish at that granularity —
Dell's `DriverPackCatalog.cab` and Lenovo's `catalogv2.xml` instead answer
"what's the one bundle for this exact system model?", keyed by a
model/system identifier read from firmware (Dell's SMBIOS systemID,
Lenovo's 4-character machine-type prefix), not per-device hardware IDs.

This is a legitimate, commonly-used alternative to per-device matching —
it's how MDT/SCCM/OSDCloud driver-pack injection works in practice — so
Waypoint models it as its own small protocol rather than stretching
`DriverSource` to cover something it wasn't designed for.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Protocol


@dataclass(frozen=True)
class DriverPack:
    """One downloadable bundle covering (ideally) every device on a specific
    system model, for a specific OS. Not a single-device `DriverCandidate` —
    resolving a `DriverPack` down to individual driver files/versions is the
    OEM installer's job, not Waypoint's, at least for this milestone."""

    pack_id: str
    model_name: str
    model_key: str  # the vendor-specific identifier this was matched on
    os_label: str
    version: str
    release_date: date | None
    url: str
    hash_algorithm: str  # e.g. "sha256", "md5" — whatever the vendor actually publishes
    hash_value: str
    size_bytes: int
    source_id: str


class ModelDriverPackSource(Protocol):
    source_id: str

    def packs_for_model(self, model_key: str) -> list[DriverPack]:
        """Return every driver pack this source has for `model_key` (a
        vendor-specific model/system identifier — see each implementation's
        docstring for exactly what that identifier is and where it comes
        from on a real machine)."""
        ...
