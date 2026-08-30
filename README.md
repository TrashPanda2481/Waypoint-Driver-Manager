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

Early scaffold. The device-matching core (`waypoint.core`), engine
orchestration (`waypoint.engine`), and local driver cache
(`waypoint.sources.local_cache`) are implemented and unit-tested. The
Windows device backend (`waypoint.platform.windows`) and Windows Update
Catalog source are written against documented APIs but not yet validated
on real hardware — that's the next milestone.

## Layout

```
src/waypoint/
  core/      # pure device/candidate matching logic — no I/O, fully tested
  sources/   # driver source plugins (local cache implemented; Windows
             # Update Catalog and OEM catalogs are follow-up milestones)
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
pip install -e ".[dev]"
pytest
```

## Trying the CLI (mock/local-cache only for now)

```bash
pip install -e .
waypoint scan --json
```

On non-Windows systems this uses `waypoint.platform.linux.LinuxDeviceBackend`,
which is a parity/testing backend, not the primary target — see
`docs/Architecture.md` section 6.
