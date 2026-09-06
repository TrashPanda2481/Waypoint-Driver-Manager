# Waypoint — TODO

Working branch: `WDI-Rewrite`. The Python tree under `src/waypoint/` stays
authoritative and untouched until the .NET port can replace it.
Last reviewed 2026-09-06.

## Cutover gates

Nothing here is optional. The .NET port cannot replace Python until all four
are done, because today it cannot enumerate a device, has no command line, and
has no window.

### 1. Windows device backend — ADR-0001 step 3

Port `platform/windows.py` (127 lines) to `Waypoint.Platform`, against
SetupAPI / CfgMgr32 and `System.Management` (`Win32_PnPEntity`,
`Win32_PnPSignedDriver`). Replace the throw in
`EngineFactory.BuildDefaultBackend()`.

Treat this as **validation work, not porting work**. Neither implementation
has ever enumerated a real device — Python's backend is written against
documented APIs and explicitly unproven. This is the first time anyone
confirms Waypoint can read a device tree at all.

Precedent for taking that seriously: `oem/cab.py` looked correct, was ported
faithfully, and was broken on Windows because only the Linux path had ever
run. Expect the same class of surprise here.

Done when: a real scan on real Windows 11 hardware returns the machine's
actual devices and bound drivers, cross-checked against Device Manager.

### 2. Port the CLI

`cli/main.py` (186 lines). The .NET CLI is a self-test that ignores its
arguments entirely — `waypoint scan --json --oem` prints sample data and
exits 0.

Needs: `scan` / `plan` / `apply` subcommands, `--json`, `--out`, `--apply`
(dry-run is the default), `--oem`, `--force-oem-refresh`, `--cache-dir`, and
the documented exit codes — 0 clean, 1 action needed, 2 error. RMM and
scripting integration depends on those codes, so they are contract, not
cosmetics.

### 3. WPF GUI — ADR-0001 step 4

`gui/app.py` + `gui/workers.py` (326 lines) to `waypoint-desktop.exe`, which
is currently a 66-line empty template. Waypoint is GUI-first by requirement
(Architecture.md 2).

Must carry over: OEM catalogs opt-in checkbox with the force-refresh
checkbox gated behind it, catalog refresh on a background thread so a
first-run ~57MB download cannot freeze the window, and the three-tier triage
tree from Architecture.md 3.1.

Then retarget the Start Menu shortcut in `packaging/Waypoint.wxs` from
`cmd /k waypoint.exe` to `waypoint-desktop.exe`.

### 4. Real code-signing certificate

Self-signed proves the pipeline only. The chain reports `UntrustedRoot`, UAC
says unknown publisher, and **AV/SmartScreen reputation is still
unvalidated** — which was the entire justification for leaving Python.

Swap is a thumbprint change: rerun `dotnet/setup-dev-signing.ps1 -Thumbprint
<real>`, or pass `-p:WaypointSignThumbprint=<real>` per publish. Then repeat
the Defender/Bitdefender comparison against the Mark-of-the-Web-tagged
artifacts.

### Then: authorize the cutover

Run .NET and Python side by side on the same machine and compare scan output
device for device. That comparison authorizes the swap — not this checklist.

## Known issues, deliberately deferred

- **`DeviceAssessment` has reference equality** where Python's dataclass has
  value equality. Nothing compares assessments yet. Converting it to a record
  does not fix it — `List<DriverCandidate>` and `List<string>` members would
  reference-compare for the same reason `Device.Hwids` did.
- **`InvariantGlobalization` is set only on `Waypoint.Cli`**, so Core is
  tested under a different globalization than it ships under. The fix is for
  Core to format dates as explicit ISO-8601 in code rather than depend on a
  host switch — lands with the CLI's JSON output.
- **No `Directory.Build.props`.** Seven projects restate `Nullable` and
  `ImplicitUsings` independently. Worth adding next time a project is created.
- **Windows Update `Version` is always `"unknown"`** in both implementations:
  `DriverVerVersion` is not a WUA property, so Python's `getattr` default
  always fires. Revisit in step 1 above, when that code first meets hardware.
- **No `LICENSE` file**, though `pyproject.toml` declares MIT. The installer
  skips its license page for this reason.
- **`HpPlatformInfo` record equality** reference-compares its
  `SupportedOsDescriptions` list — same trap as `Device.Hwids`, not yet load-
  bearing.

## Bugs left in the Python on purpose

The .NET port fixed these; Python keeps them until it is retired. Both are
recorded in ADR-0001 under deliberate divergences.

- **`rank_candidates` picks the oldest driver** in the best trust tier —
  ascending date sort, contradicting its own docstring. Verified: given a 2026
  and a 2020 WHQL driver it returns the 2020 one.
- **`oem/cab.py` CAB extraction is broken on Windows.** `expand.exe` refuses
  to expand a file onto itself yet exits 0, and renames single-member payloads
  after the cab. Every OEM catalog refresh would fail. Never caught because
  live validation ran `cabextract` on Linux.

## Open questions

- Scheduled/background catalog refresh — currently caller-triggered only.
  Deliberately no service or scheduled task today (Architecture.md 6).
- HP per-device matching would need a WQL evaluator over their WSUS SDP feed.
  Judged out of scope; `HpPlatformCatalogSource` answers platform lookup only.
- Distribution channel for releases (GitHub Releases vs elsewhere) — artifacts
  build locally via `dotnet/packaging/build-package.ps1`, nothing publishes
  them yet.
