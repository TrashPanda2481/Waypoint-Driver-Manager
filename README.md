# Waypoint Driver Manager

A driver detection, sourcing, and install manager for Windows built to fix
the structural problems in tools like Snappy Driver Installer — not just
give them a new coat of paint. Cross-platform core, GUI-first client,
CLI-first for IT-toolchain automation.

See [`docs/Architecture.md`](docs/Architecture.md) for the full design
rationale, including a point-by-point mapping from SDI's known failure
modes (sourced from public reviews, forums, and issue trackers) to specific
Waypoint design decisions.

## Status

The device-matching core (`waypoint.core`), engine orchestration
(`waypoint.engine`), local driver cache (`waypoint.sources.local_cache`),
and the GUI's scan wiring (`waypoint.gui`) are implemented and covered by
automated tests (23 passing, including a headless Qt test of the actual
scan button -> background thread -> engine -> tree-view path). The GUI and
CLI now share one backend/source construction path via
`waypoint.engine.factory.build_default_engine()`, so they can't silently
drift apart.

OEM catalog sourcing (`waypoint.sources.oem`) is implemented for Dell
(per-device `CatalogPC.cab` as a full `DriverSource`, plus the model-keyed
`DriverPackCatalog.cab`), Lenovo (model-keyed `catalogv2.xml`), and a
deliberately scoped-down HP platform-support lookup (`platformList.cab`) —
see [`docs/Architecture.md`](docs/Architecture.md) section 3.2 for exactly
what's real vs. out of scope per vendor, with source URLs. Tested against
small real-data fixtures trimmed from each vendor's actual live catalog
(not fabricated); an end-to-end download-and-parse of a *full* real catalog
has not been wired into an automated test. Not included in the default
source list — opt in via `waypoint.engine.factory.build_oem_sources()`.

The Windows device backend (`waypoint.platform.windows`) and Windows Update
Catalog source are written against documented APIs but not yet validated
on real hardware — that's the next milestone. Until then, running on
Windows will exercise real code paths but is unverified; running on Linux
exercises the parity backend, useful for GUI/engine development only.

## Layout

```
src/waypoint/
  core/      # pure device/candidate matching logic — no I/O, fully tested
  sources/   # driver source plugins (local cache + OEM catalogs implemented;
             # Windows Update Catalog source is written but not yet
             # validated on real hardware)
  sources/oem/  # Dell/Lenovo/HP catalog sources (see docs/Architecture.md 3.2)
  engine/    # scan -> plan -> backup -> install -> rollback orchestration
             # + append-only JSON-Lines audit log
  platform/  # OS backends behind one interface (mock, windows, linux)
  cli/       # scriptable entry point (waypoint scan / plan / apply)
  gui/       # PySide6 client of the engine (skeleton, not yet wired to a
             # live backend)
tests/       # unit tests for core matching + engine safety behavior
docs/        # architecture spec and design decisions
```

## Running the tests

```bash
pip install -e ".[dev,gui,linux]"   # add [windows] instead of [linux] on Windows
QT_QPA_PLATFORM=offscreen pytest    # offscreen avoids needing a real display for GUI tests
```

## Trying the CLI

```bash
pip install -e ".[linux]"   # or ".[windows]" on Windows
waypoint scan --json
```

## Trying the GUI

```bash
pip install -e ".[gui,linux]"   # or ".[gui,windows]" on Windows
waypoint-gui
```

On non-Windows systems both use `waypoint.platform.linux.LinuxDeviceBackend`,
which is a parity/testing backend, not the primary target — see
`docs/Architecture.md` section 6.
