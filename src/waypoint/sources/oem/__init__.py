"""OEM catalog driver sources.

OEM vendors publish two genuinely different kinds of catalog, and Waypoint
treats them as two different things rather than forcing one shape onto both
(see docs/Architecture.md section 3.2 for the pluggable-source rationale):

1. Per-device catalogs — a flat list of (hardware ID -> driver) entries that
   plug directly into the existing `DriverSource` protocol used by
   `local_cache` and `windows_update`. As of this writing, Dell is the only
   OEM that publishes this shape in a clean, declarative form
   (`downloads.dell.com/catalog/CatalogPC.cab`, PCI vendor/device IDs per
   component). See `dell_catalog.py`.

2. Per-model driver packs — "give me the one bundle for this exact system
   model," keyed by a model/system identifier rather than a hardware ID.
   Dell (`DriverPackCatalog.cab`) and Lenovo (`catalogv2.xml`) both publish
   this shape cleanly. See `model_pack.py`, `dell_driverpack.py`,
   `lenovo_driverpack.py`.

HP's public catalog (`HpCatalogForSms.latest.cab`) is neither: it's a WSUS
Software Distribution Package feed whose applicability is expressed as WQL
queries evaluated against live system state (see `hp_platform.py` for what
is and is not implemented, and why).
"""
