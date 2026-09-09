# Waypoint — TODO

Working branch: `WDI-Rewrite`. The Python tree under `src/waypoint/` stays
authoritative and untouched until the .NET port can replace it.
Last reviewed 2026-09-06.

## Cutover gates

Nothing here is optional. The .NET port cannot replace Python until all four
are done, because today it cannot enumerate a device, has no command line, and
has no window.

### 1. Windows device backend — ADR-0001 step 3 — ENUMERATION DONE 2026-09-09

`WindowsDeviceBackend` reads the tree through **CfgMgr32 P/Invoke**, not WMI.
`System.Management` is neither trim- nor AOT-safe and the CLI publishes with
Native AOT; a bulk `Win32_PnPSignedDriver` query also costs 2.4s, and
per-device CIM property reads take minutes across the tree. CfgMgr32 does the
whole enumeration in **781ms**. `BuildDefaultBackend()` no longer throws.

Validated on real Windows 11 hardware:
- **233 devices, exact set match** against `Get-PnpDevice` — 0 missing, 0
  extra. The 350-vs-233 gap is fully accounted for: 105 not present (excluded
  by `CM_GETIDLIST_FILTER_PRESENT`) and 12 present with no hardware ID
  (skipped, matching the Python).
- 0 friendly-name and 0 class mismatches across all 233.
- Driver version, date, provider and INF populated for all 233.
- Signature agreed with WMI `IsSigned` on **233/233** — no false alarms, no
  missed warnings.
- The **Native AOT** binary produces the identical 233 with **0 IL warnings**,
  confirmed by forcing the trimmer to analyse it (+356KB when reachable).

**Still unverified — do not claim these work:**
- The **unsigned** signature branch never fired. All 233 present devices are
  signed; the machine's 3 WMI-unsigned drivers are non-present with no INF.
  Needs a machine with a genuinely unsigned present driver.
- `CreateRestorePoint` — needs elevation and System Restore enabled.
- `InstallDriver` with `dryRun: false` — would modify the system.
- `ExportDriverBackup` success path — only the no-INF refusal is covered.
- **WHQL is unreachable by design.** WHQL and attestation share the signer
  "Microsoft Windows Hardware Compatibility Publisher"; telling them apart
  needs catalog inspection, so a signed driver reports `Attestation` rather
  than asserting unverified trust. Architecture.md 3.2 already flags this.

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
