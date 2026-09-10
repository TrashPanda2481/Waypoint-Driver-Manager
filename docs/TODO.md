# Waypoint — TODO

The .NET implementation is `main`. The original Python is preserved unmaintained
on `python-reference`; it is still the only Windows Update source, so it stays
as a porting reference until that lands.
Last reviewed 2026-09-09.

## Cutover gates

Three of the four are done: it reads the device tree, has a working command
line, and has a window. What remains is a certificate anyone can trust.

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

### 2. Port the CLI — DONE 2026-09-09

`scan` / `plan` / `apply` with `--json`, `--out`, `--apply`, `--oem`,
`--force-oem-refresh`, `--cache-dir`, `--audit-log`, `--min-signature`, plus
`--confirm` / `--confirm-all` / `--backup-dir` for apply. Argument parsing is
hand-rolled: the surface is three verbs and a handful of flags, and a parsing
dependency would have to earn its place in an AOT binary.

Verified on real hardware: `waypoint scan` returns 233 devices, flags 103 as
ambiguous (shared hardware IDs — the SDI failure mode this exists to catch),
`--json` emits the Python's exact key set, and the exit contract holds —
0 clean, 1 action needed (forced with `--min-signature whql`, which nothing
satisfies by design), 2 error. The signed Native AOT binary does the same
scan with 0 IL warnings.

**`apply` is implemented rather than ported.** `cli/main.py`'s `cmd_apply` is
a stub that prints an error and returns 2; the engine's `Apply` has since
been written and tested, and it enforces confirmation itself, so the CLI
wires it up. Plan-file replay stays out of scope in both: a plan on disk
carries no `Device` objects to install onto.

Still untested end to end: `apply --apply` against a real driver, which needs
a candidate in the local cache and would modify the machine.

### 3. WPF GUI — ADR-0001 step 4 — DONE 2026-09-09

`waypoint-desktop.exe` is built and scanning. Structure follows
Architecture.md 3.1 rather than the Python, which had implemented only part
of it: tier → setup class → device, and a diff card comparing installed
against candidate field by field, where the Python grouped by tier alone and
showed one flat row.

Scanning runs through `Task.Run`, so the worker and thread lifetime
bookkeeping `gui/workers.py` warns about has no equivalent. OEM sources build
a second engine for the one scan rather than being pushed onto the live
engine and stripped again in a `finally`, matching what `Commands.Build`
already does and removing the duplicate-source hazard.

Two additions the Python did not have. **Show up-to-date devices**, because
without it a healthy machine renders a blank pane — every device here is up
to date, so all three tiers read (0) — and because it is the only route to
the ambiguous devices, which are the clearest advantage over SDI. And
**source failures in the status bar**, so an incomplete scan cannot read as a
complete one.

Driving it through UI Automation caught two bugs a screenshot would not: tree
rows exposed `Waypoint.Gui.TreeNode` as their accessible name, and
`IsExpanded` was bound nowhere, so the tiers 3.1 wants open stayed shut.

Validated on real hardware: 233 devices, 24 setup classes, 103 ambiguous,
matching `waypoint scan` exactly. 16 tests, 131 across the solution.

**Not done in the window:** applying a plan. Scan and inspect only; `apply`
stays CLI-only until a real install has been performed at least once.

#### Packaging — two variants

WPF has no Native AOT story and its binding stack is reflection-based, so the
GUI ships on the runtime while `waypoint.exe` stays AOT. That is a real
size decision, so both shapes ship and the filename says which is which:

| Artifact | Size | Needs |
| --- | --- | --- |
| `waypoint-installer-<v>-win-x64.msi` | 67 MB | nothing |
| `waypoint-installer-<v>-win-x64-requires-dotnet8.msi` | 11 MB | .NET 8 Desktop Runtime |
| `waypoint-portable-<v>-win-x64.zip` | 70 MB | nothing |
| `waypoint-portable-<v>-win-x64-requires-dotnet8.zip` | 4 MB | same runtime |

`waypoint.exe` is byte-identical across all four and depends on nothing, so
the command line works on a bare machine either way — which matters, because
the machine that needs a driver tool most is the one whose NIC has no driver
and cannot go fetch a runtime.

GUI files are harvested with WiX's `<Files Include>` rather than listed: the
bundled build is ~150 files of .NET and a hand-maintained list would rot on
every SDK bump. `Exclude` is not an attribute in WiX 5, so `build-package.ps1`
clears PDBs out of the staging directory before harvesting.

Two signing fixes went with it. `Directory.Build.targets` matched only
`OutputType == Exe`, so the GUI — a `WinExe` — would have shipped unsigned.
And `MajorUpgrade` now sets `AllowSameVersionUpgrades`, so switching between
the two variants of one version replaces rather than stacking.

Start Menu now carries two entries: **Waypoint Driver Manager** →
`waypoint-desktop.exe`, and **Waypoint Command Line** → `cmd /k waypoint.exe
--help`. Verified by administrative extract and by reading the MSI's Shortcut
table; neither variant has been installed on a live machine yet.

**Next:** cut a release with the four artifacts and publish their hashes.
`docs/INSTALL.md` now describes them but no release ships them yet.

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

## Model-keyed driver packs — DONE 2026-09-09

Recovered from the abandoned `rust-rewrite` branch (tag `archive/rust-rewrite`)
and implemented. `waypoint driverpack [--json] [--force-oem-refresh]` answers
"what bundle does the vendor publish for this system model" — a different
question from `scan`, and how MDT/SCCM driver injection actually works.

Before this, `DellDriverPackSource`, `LenovoDriverPackSource` and
`HpPlatformCatalogSource` were implemented, tested and **unreachable**: nothing
in the engine or CLI referenced `IModelDriverPackSource` at all.

What landed:

- **`SystemModelDetector`** reads SMBIOS through `GetSystemFirmwareTable`
  rather than WMI, so it stays AOT-clean. Parses Type 1 (system) and Type 2
  (baseboard). Verified against this machine: identical values to
  `Win32_ComputerSystem`, no WMI.
- **Placeholder filtering.** Firmware ships `"Default string"`,
  `"To be filled by O.E.M."` and friends instead of leaving fields blank —
  this machine's `SystemSKUNumber` is literally `"Default string"`. Those
  normalize to empty rather than being sent to a vendor catalog as a model name.
- **Dell indexes on model name as well as systemID.** Dell's KB says systemID
  "is not readily accessible [via a] WMI query" and recommends name matching on
  Windows; combined with placeholder SKUs, name is the key that works on real
  hardware. Lookup tries SKU first, then name, then baseboard.
- **`EngineFactory.BuildOemModelPackSources()`**, companion to
  `BuildOemSources()`. Construction only, no download.
- **Partial-failure resilient.** One vendor's refresh failing warns to stderr
  and continues with the others, the same posture `Scan` takes.

Validated against live vendor catalogs: Dell's `DriverPackCatalog.cab` and HP's
`platformList.cab` both extracted correctly — the first time the CAB fix has run
against real vendor cabs rather than a generated fixture — plus Lenovo's plain
XML. Three catalogs fetched, parsed and queried in 3.4s.

### SMBIOS override — DONE 2026-09-09

The lookup could not be exercised here: this is a Gigabyte board and no vendor
publishes packs for it, so a real hit needed a Dell/Lenovo/HP machine or a VM
spoofing SMBIOS. `--model-manufacturer / --model-product / --model-sku /
--model-baseboard` remove that dependency — they ask the catalogs about another
machine from this one.

All-or-nothing by design: any one of them replaces every SMBIOS field, so a run
can never blend real and supplied values into a system that does not exist. Each
run prints a stderr note that the answer is not about this machine, and the
flags are rejected outside `driverpack`, where a supplied model would not change
the answer.

Live results, this machine, against the real catalogs:

| Query | Result |
| --- | --- |
| `--model-product "OptiPlex 5070"` | 2 packs, Win10 A10 + Win11 A00, real URLs and SHA-256 |
| `--model-product "20HD0000US"` | ThinkPad T470, 6 releases 1703–1909 |
| `--model-baseboard 8079` | HP EliteBook 840 G3 Notebook PC |
| `--model-baseboard 8references` | correctly nothing |

**One bug this caught.** Every Dell pack was listed twice. Indexing walks each
`<Model>` under `SupportedSystems`, and release PPPRC covers the 5070 under both
`092F` and `0932` — so the name key received the same download once per
systemID. Fixed by de-duplicating on releaseID per key. A test asserted the old
behaviour (`byName.Count == by092F.Count + by0932.Count`); the honest invariant
is that the name key is the *union* of what each systemID reaches, not the sum,
and it now asserts that instead.

Also fixed: `--force-oem-refresh` warned "no effect without --oem" on
`driverpack`, which does use it; and a machine identified in HP's platform list
reported "no driver packs published for this model" — HP publishes those through
Image Assistant, not this catalog, so it now says so.

**Still unvalidated:** a real machine of each vendor confirming that SMBIOS
reports the strings these overrides supply. The lookup is proven; the reading of
firmware on Dell/Lenovo/HP hardware is not.

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
- **Windows Update Catalog source is unreachable under AOT.** `PublishAot`
  sets `System.Runtime.InteropServices.BuiltInComInterop.IsSupported=false`,
  so its `[ComImport]` activation throws "Built-in COM has been disabled" in
  the configuration Waypoint ships — it was dropped from
  `BuildDefaultSources` because every scan would otherwise report a failed
  source. Reaching Windows Update needs a `ComWrappers` rewrite
  (`[GeneratedComInterface]`). Note the source was never validated against
  real hardware and its `Fetch` still throws, so nothing working was lost.
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
