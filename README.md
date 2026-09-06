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
automated tests (36 passing, including a headless Qt test of the actual
scan button -> background thread -> engine -> tree-view path). The GUI and
CLI now share one backend/source construction path via
`waypoint.engine.factory.build_default_engine()`, so they can't silently
drift apart.

OEM catalog sourcing (`waypoint.sources.oem`) is implemented for Dell
(per-device `CatalogPC.cab` as a full `DriverSource`, plus the model-keyed
`DriverPackCatalog.cab`), Lenovo (model-keyed `catalogv2.xml`), and a
deliberately scoped-down HP platform-support lookup (`platformList.cab`) —
see [`docs/Architecture.md`](docs/Architecture.md) section 3.2 for exactly
what's real vs. out of scope per vendor, with source URLs. The automated
test suite runs against small real-data fixtures trimmed from each
vendor's actual live catalog (not fabricated) — deliberately, so CI
doesn't depend on Dell's/Lenovo's/HP's servers or spend network/credit
cost on every run. Not included in the default source list — opt in via
`waypoint.engine.factory.build_oem_sources()`.

All four OEM `refresh()` pipelines have now been manually run once
against the live network (2026-08-30, one-off validation runs, not added
to the automated suite — CI still uses the offline fixtures deliberately):
- **Dell** (`CatalogPC.cab`): real download -> `cabextract` -> XML parse ->
  8,655 hardware IDs / 159,338 candidates in ~1.5s. Then a real `fetch()`
  of a real 10.37MB driver package, MD5-verified against the catalog's
  published hash (`450566a766e982f351f918cce26eb2e4`, independently
  re-checked with `md5sum`), then SHA-256-computed.
- **Lenovo** (`catalogv2.xml`, no cab): real download -> XML parse ->
  1,475 machine-type codes / 8,674 driver-pack entries in ~0.2s. Spot-check
  on machine-type `10M4` matched the offline fixture exactly. The
  referenced driver-pack download itself (~300MB per a `HEAD` check) was
  not downloaded to verify its hash end-to-end — too large to justify for
  a spot-check, so the "crc is actually SHA-256" claim rests on the
  64-character length match for this vendor, not a hashed download.
- **HP** (`platformList.cab`): real download -> `cabextract` -> XML parse
  -> 603 real SystemIDs in ~0.1s. Spot-check on SystemID `1909` matched
  the offline fixture exactly (`HP ZBook 15 Mobile Workstation`).
- **Dell driver-pack** (`DriverPackCatalog.cab`, the model-keyed one):
  real download -> `cabextract` -> XML parse -> 744 systemIDs / 1,580
  driver-pack entries in ~0.4s. Spot-check on systemID `092F` matched the
  offline fixture exactly. The real SHA-256 claim for this catalog *was*
  verified end-to-end this time: the fixture's own packs were ~2.5GB each
  (too large to download), so a different, much smaller real pack from
  the same live catalog (11.74MB) was downloaded and independently hashed
  with `sha256sum` -- matched the catalog's published value exactly.

All four OEM source implementations (Dell per-device, Dell driver-pack,
Lenovo, HP) have now had their `refresh()` pipelines validated against
live data.

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

## The .NET port (in progress)

Waypoint is being reimplemented in C# / .NET 8 — Windows-first, WPF GUI,
signed self-contained binaries. Rationale and the phased plan are in
[`docs/ADR-0001-language-migration-python-to-dotnet.md`](docs/ADR-0001-language-migration-python-to-dotnet.md);
what is left before the port can replace Python is in
[`docs/TODO.md`](docs/TODO.md).
The Python tree above remains authoritative and runnable until each module
has a validated .NET replacement.

```
dotnet/
  Waypoint.Core/       # models, matching, and the shared contracts
                       # (IDriverSource, IModelDriverPackSource, IDeviceBackend)
  Waypoint.Sources/    # local cache + Dell/Lenovo/HP OEM catalogs
                       # + Windows Update (COM, unvalidated)
  Waypoint.Engine/     # audit log, plan, scan/plan/apply orchestration, factory
  Waypoint.Platform/   # OS backends — mock only; Windows backend is step 3
  Waypoint.Cli/        # AOT smoke test only — not yet a port of cli/main.py
  Waypoint.Gui/        # WPF skeleton, not yet started
  *.Tests/             # xUnit, incl. the real vendor catalog fixtures
```

```bash
cd dotnet
dotnet test                                  # 60 tests
dotnet publish Waypoint.Cli -c Release -r win-x64   # ~3MB standalone exe
```

Native AOT publish needs `vswhere.exe` on `PATH`
(`C:\Program Files (x86)\Microsoft Visual Studio\Installer`) and the MSVC
C++ toolchain.

### Packaging

```powershell
pwsh dotnet/packaging/build-package.ps1
```

Produces both distribution shapes into `dotnet/packaging/artifacts/`:

| Artifact | Use |
|---|---|
| `waypoint-portable-<ver>-<rid>.zip` | Unzip and run. Nothing installed, nothing on PATH. |
| `waypoint-installer-<ver>-<rid>.msi` | Per-machine install to `C:\Program Files\Waypoint`, added to system PATH, Start Menu entry, removable from Add/Remove Programs. |

Naming: `waypoint.exe` is the CLI, `waypoint-desktop.exe` is the GUI, and
`waypoint-installer-*` is whatever carries them onto a machine.

Installing (needs elevation, since it is per-machine):

```powershell
msiexec /i waypoint-installer-0.1.0-win-x64.msi          # or double-click for the wizard
msiexec /i waypoint-installer-0.1.0-win-x64.msi /qn      # silent, for Intune/SCCM/GPO/PDQ
msiexec /x waypoint-installer-0.1.0-win-x64.msi /qn      # uninstall
```

After installing, `waypoint` resolves in any **new** shell — PATH changes do
not reach already-open ones. Both the exe and the MSI are signed. Either way
state lives in `C:\ProgramData\Waypoint` (cache + audit log); the MSI leaves
it in place on uninstall so a reinstall keeps the technician's driver cache.

### Signing

One-time setup per machine — creates a self-signed development certificate
and points the build at it:

```powershell
pwsh dotnet/setup-dev-signing.ps1
```

After that `dotnet publish` signs automatically. The certificate is referenced
by thumbprint from `CurrentUser\My`, so no key material is ever stored in the
repo; the generated `Directory.Build.local.props` is gitignored and machine-local.
Without it, publishing still succeeds — just unsigned, with a message saying so.

Thumbprint precedence is `/p:WaypointSignThumbprint` > `WAYPOINT_SIGN_THUMBPRINT`
> the local props file, so a real certificate can override the dev one per
invocation without editing anything:

```bash
dotnet publish Waypoint.Cli -c Release -r win-x64 -p:WaypointSignThumbprint=<real-thumbprint>
```

Signatures are SHA-256 and RFC3161-timestamped, so they stay valid past cert
expiry. Signing is wired after AOT's `CopyNativeBinary` step — that target
replaces the apphost in the publish directory, so signing on `Publish` would
be silently overwritten.

The current cert is **self-signed and for development only**. It proves the
signing pipeline, not distribution trust: the chain reports `UntrustedRoot`
and SmartScreen/AV reputation is unvalidated. Moving to a real OV/EV
certificate is a thumbprint change and nothing else — rerun the setup script
with `-Thumbprint <real>`, or pass it per invocation as above.

## Running the tests

```bash
pip install -e ".[dev,gui,linux]"   # add [windows] instead of [linux] on Windows
QT_QPA_PLATFORM=offscreen pytest    # offscreen avoids needing a real display for GUI tests
```

## Trying the CLI

```bash
pip install -e ".[linux]"   # or ".[windows]" on Windows
waypoint scan --json

# Opt-in: also query Dell's real per-device catalog (CatalogPC.cab).
# First run downloads ~57MB; later runs against the same --cache-dir
# reuse the on-disk copy instead of re-downloading it. Off by default.
waypoint --oem scan --json

# Force a fresh download instead of reusing the cached catalog (e.g. for
# a scheduled job that wants current Dell data). No effect without --oem
# -- prints a warning to stderr rather than silently doing nothing.
waypoint --oem --force-oem-refresh scan --json
```

## Trying the GUI

```bash
pip install -e ".[gui,linux]"   # or ".[gui,windows]" on Windows
waypoint-gui
```

The main window has an "Include OEM catalogs" checkbox above the device
tree — the GUI equivalent of `--oem` above. Unchecked (the default), a
scan behaves exactly as before the checkbox existed. Checked, the Dell
catalog refresh runs on a background thread so a first-time ~57MB
download doesn't freeze the window; later scans against the same cache
directory reuse the on-disk copy.

A second checkbox, "Force refresh (ignore cached catalog)", is the GUI
equivalent of `--force-oem-refresh`. It stays disabled and unchecked
unless "Include OEM catalogs" is also checked, so it can never be left
checked in a state where it would do nothing.

On non-Windows systems both use `waypoint.platform.linux.LinuxDeviceBackend`,
which is a parity/testing backend, not the primary target — see
`docs/Architecture.md` section 6.
