# Waypoint Driver Manager — Architecture & Design Spec

## 1. Why This Exists

Snappy Driver Installer (SDI/SDIO) is functional but structurally dated, and the
weaknesses aren't cosmetic — they're architectural:

| Problem observed in SDI/SDIO/DriverPack Solution | Source |
|---|---|
| Monolithic offline driver packs, 9–60 GB, distributed over BitTorrent | [SourceForge reviews](https://sourceforge.net/projects/snappy-driver-installer/reviews/), [Ed Tittel](https://www.edtittel.com/blog/working-sdio-driver-updates.html) |
| HWID matching picks wrong/generic drivers, installs devices that don't exist (e.g. two touchpads), overwrites working OEM drivers with worse generic ones | [SourceForge ticket #108](https://sourceforge.net/p/snappy-driver-installer/tickets/108/), [SuperUser](https://superuser.com/questions/1426838/why-does-snappy-driver-installer-recommend-so-many-driver-updates) |
| "Not even System Restore" can undo a bad batch install | [SourceForge ticket #108](https://sourceforge.net/p/snappy-driver-installer/tickets/108/) |
| Unwanted/unselected drivers installed silently (SDIO Huawei case) | [Reddit r/pcmasterrace](https://www.reddit.com/r/pcmasterrace/comments/1iymiql/psa_snappy_driver_installer_origin_the_alleged/) |
| Fragmented, low-trust lineage (SDI → SDIO → DriverPack Solution forks), inconsistent pack quality between forks | [Technibble forum](https://www.technibble.com/forums/threads/snappy-driver-installer.60557/page-3) |
| No cryptographic pinning of packs beyond ad-hoc WHQL `sigverif` checks | [Technibble](https://www.technibble.com/snappy-driver-installer/) |
| Dated UI, opaque progress, "select all and hope" workflow | [CNET](https://download.cnet.com/snappy-driver-installer/3000-windows-snappy-driver-installer.html), [iObit review](https://www.iobit.com/en/snappy-driver-installer.html) |

Waypoint is designed against this list directly: every item above maps to a
specific design decision below, not a vague "make it nicer" goal.

## 2. Product Identity

- **Name:** Waypoint Driver Manager
- **Branding family:** Meridian (shares the amber `#E8760A` / compass-adjacent
  naming convention) but is a **standalone product**, not a Meridian OS
  component — built for a general IT toolchain (techs re-imaging Windows
  machines, MSPs, sysadmins scripting deployments), Windows-first with a
  Linux-compatible core.
- **Tagline concept:** "Know exactly where a driver came from, and exactly how
  to get back."

## 3. Core Design Principles (mapped to pain points)

### 3.1 UI/UX & Organization
- Devices are grouped by **PnP Setup Class** (Display, Net, Media, HDC,
  Bluetooth, Printer, USB, SCSIAdapter, etc.) — the same taxonomy Windows
  itself uses in Device Manager — not by opaque "driverpack" archive names.
- Three-tier triage instead of one big checkbox list:
  1. **Missing** — no driver bound, device shows an error code. Always shown expanded.
  2. **Problem** — driver bound but device reports a Code 1/10/28/43 error, or driver is unsigned/untrusted per policy.
  3. **Upgrade available** — device works fine, a newer candidate exists. Collapsed by default, never auto-checked.
- Every candidate driver is shown as a **diff card**: currently installed
  (version, date, publisher, signature) vs. candidate (same fields) + which
  source it came from. No install proceeds from a summary number alone.
- Ambiguous matches (same HWID/class matching multiple installed devices)
  are flagged and require explicit per-device confirmation — never
  auto-resolved by "best guess."

### 3.2 Driver Database & Sourcing
Instead of one monolithic offline archive, Waypoint uses a **pluggable source
model**. Each source implements the same interface and returns structured,
versioned candidate metadata — never a blind archive to extract-and-hope:

```csharp
// Waypoint.Core/Sources.cs
public interface IDriverSource
{
    string SourceId { get; }
    IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids);        // metadata only — must not download
    Task<string> FetchAsync(DriverCandidate c, string destDir, CancellationToken ct = default); // throws on hash mismatch
}

// Waypoint.Core/Models.cs
public sealed record DriverCandidate(
    string Hwid, string ClassGuid, string Version, DateOnly? DriverDate, string Publisher,
    SignatureType SignatureType,   // Unsigned | TestSigned | Attestation | Whql — declared weakest-first, so default() == Unsigned
    string Sha256, long SizeBytes, string SourceId, string SourceUrl, string DownloadUri);
```

Sources (each independently toggle-able):
- **Windows Update Catalog** — via `Microsoft.Update.Session` COM search
  (`IsInstalled=0 and Type='Driver'`), the same official channel Windows
  Update itself uses. [Microsoft Q&A](https://learn.microsoft.com/en-ca/answers/questions/5656657/microsoft-update-catalog-searching-for-firmware-up)
  **Status: not yet available in the shipped build.** The port
  (`Waypoint.Sources/WindowsUpdateCatalogSource.cs`) carries the WUA COM
  interface declarations but is dead code under Native AOT: `PublishAot` sets
  `BuiltInComInterop.IsSupported=false`, so `[ComImport]` activation throws,
  and it is excluded from `EngineFactory.BuildDefaultSources()` rather than
  reporting a failed source on every scan. Reaching Windows Update needs a
  `ComWrappers` / `[GeneratedComInterface]` rewrite. The only *working* Windows
  Update source today is the one on the `python-reference` branch. This is the
  subject of [ADR-0003](ADR-0003-driver-sourcing.md) (driver-sourcing strategy,
  Proposed); see also `TODO.md`.
- **OEM vendor catalogs** — implemented for Dell, Lenovo, and (scoped down)
  HP. Verified by downloading and inspecting each vendor's real published
  feed (2026-08-30), not assumed from documentation:
  - **Dell per-device catalog** (`Waypoint.Sources/Oem/DellCatalogSource.cs`,
    `DellCatalogSource`) — [`CatalogPC.cab`](https://downloads.dell.com/catalog/CatalogPC.cab),
    the same feed Dell Command | Update and SCCM third-party-update
    workflows consume. Genuinely per-hardware-ID: each driver component
    lists PCI `vendorID`/`deviceID`/`subVendorID`/`subDeviceID` pairs,
    matched into the same `IDriverSource.Search(hwids)` interface every
    other source implements. Only MD5 is published per-component (no
    SHA-256) — `Search()` honestly returns `Sha256 = ""`, and `FetchAsync()`
    verifies against the published MD5 before computing and returning a
    real SHA-256 of the verified bytes.
  - **Dell driver-pack catalog** (`Waypoint.Sources/Oem/DellDriverPackSource.cs`,
    `DellDriverPackSource`) — [`DriverPackCatalog.cab`](https://downloads.dell.com/catalog/DriverPackCatalog.cab),
    keyed by Dell's SMBIOS `systemID`, not by hardware ID — a different
    shape (see `IModelDriverPackSource` below), matching how MDT/SCCM
    driver-pack injection actually works. Publishes real SHA-256 per
    package.
  - **Lenovo driver-pack catalog** (`Waypoint.Sources/Oem/LenovoDriverPackSource.cs`,
    `LenovoDriverPackSource`) — [`catalogv2.xml`](https://download.lenovo.com/cdrt/td/catalogv2.xml),
    keyed by the 4-character machine-type prefix read from a real
    system's SMBIOS product name. Its `crc` attribute is actually a
    SHA-256 digest (64 hex chars) despite the name — reported honestly in
    the candidate's `Sha256` field, not taken at face value as a CRC32.
  - **HP platform support list** (`Waypoint.Sources/Oem/HpPlatformCatalogSource.cs`,
    `HpPlatformCatalogSource`) — [`platformList.cab`](https://hpia.hpcloud.hp.com/ref/platformList.cab),
    **deliberately scoped down**: only answers "is this SystemID a known
    HP platform, and for which OS versions" — not an `IDriverSource` or
    `IModelDriverPackSource`. HP's actual per-update applicability feed
    ([`HpCatalogForSms.latest.cab`](https://hpia.hpcloud.hp.com/downloads/sccmcatalog/HpCatalogForSms.latest.cab))
    turned out to be a WSUS Software Distribution Package (SDP) format —
    applicability is expressed as arbitrary WQL (`bar:WmiQuery` against
    `Win32_ComputerSystem`/`Win32_BaseBoard`), not a flat HWID/model list.
    Building a WQL evaluator to fake per-device matching on top of that
    was judged out of scope and dishonest to attempt partially, so
    `HpPlatformCatalogSource` explicitly does not try — see its type docstring.
  - **Separate interface for model-keyed sources** (`Waypoint.Core/Sources.cs`,
    `IModelDriverPackSource` + the `DriverPack` record) — Dell's and Lenovo's
    driver-pack catalogs answer "what's the one bundle for this system
    model" rather than "what candidates exist for this hardware ID",
    which doesn't fit `IDriverSource`. Modeled as its own small interface
    rather than stretching `IDriverSource` to cover a shape it wasn't
    designed for.
  - Not included in `EngineFactory.BuildDefaultSources()` — OEM catalog
    refresh is real, sizeable network I/O (Dell's `CatalogPC.xml` alone is
    ~57MB uncompressed) that shouldn't fire on every default scan. Exposed
    instead through an explicit opt-in, `Waypoint.Engine/EngineFactory.cs`'s
    `BuildOemSources()` (and `BuildOemModelPackSources()` for the model-keyed
    catalogs).
  - **Opt-in wiring.** OEM catalogs never load on a default scan. `waypoint
    --oem scan`/`plan` adds `EngineFactory.BuildOemSources()` to the engine's
    source list; today that is `DellCatalogSource`, the only OEM source with
    the `IDriverSource` shape (see the model-keyed note above for why the
    others aren't). The model-keyed catalogs are reached through a separate
    verb, `waypoint driverpack`, backed by `BuildOemModelPackSources()`. Each
    invocation refreshes once: the first `--oem` run against a given
    `--cache-dir` pays the ~57MB Dell download, later runs against the same
    `--cache-dir` reuse the on-disk `CatalogPC.xml`.
  - **GUI opt-in.** `Waypoint.Gui/MainWindow.xaml.cs` carries an "Include OEM
    catalogs" checkbox, off by default — the GUI equivalent of `--oem`. The
    scan runs on a background task (`ScanRunner`, via `Task.Run`), so a
    first-time ~57MB download can't freeze the window. When OEM is enabled the
    scan builds a *second* engine for that one run rather than pushing a source
    onto the live engine and stripping it afterward, so there is no
    duplicate-source hazard and unchecking the box simply takes effect on the
    next scan. GUI and CLI share one cache root when pointed at the same
    location.
  - **Force refresh.** CLI `--force-oem-refresh` and the GUI's "Force refresh
    (ignore cached catalog)" checkbox re-download the catalog even when a
    cached copy exists. On `scan`/`plan` the flag is a no-op without `--oem`
    (the CLI warns to stderr; the GUI checkbox is disabled and unchecked
    whenever OEM is off); on `driverpack`, which always refreshes a model
    catalog, it is honoured. Routine scans should leave it off — it exists for
    a stale cache (e.g. a scheduled job wanting fresh vendor data), not as a
    default.
  - **Live-network validation of the OEM pipelines (2026-08-30, manual,
    Python-era, not part of the automated suite).** These runs validated the
    download → extract → parse → match → fetch → verify pipeline against the
    real vendor services on the original Python implementation; the .NET CAB
    path was re-validated separately against real vendor cabs (see `TODO.md`,
    "Model-keyed driver packs"). The vendor-data findings below — hashes,
    counts, spot-checks — are what those runs recorded and still hold.
    `DellCatalogSource.refresh(force=True)` run against
    the real `https://downloads.dell.com/catalog/CatalogPC.cab` —
    downloaded, `cabextract`-ed, and parsed in ~1.5s; produced 8,655
    distinct hardware IDs / 159,338 total candidates from the real 57.6MB
    `CatalogPC.xml`. Followed by a real `fetch()` of the smallest indexed
    candidate (a 10.37MB driver package): downloaded, MD5-verified against
    the catalog's published hash (`450566a766e982f351f918cce26eb2e4`,
    independently re-checked with `md5sum` outside the code path), then
    SHA-256-computed. Confirms the full download -> extract -> parse ->
    match -> fetch -> verify pipeline works end-to-end against the real
    service, not just the offline fixtures.
  - **Live-network validation, continued (2026-08-30):**
    `LenovoDriverPackSource.refresh(force=True)` run against the real
    `https://download.lenovo.com/cdrt/td/catalogv2.xml` (plain XML, no
    cab) — downloaded and parsed in ~0.2s, indexing 1,475 distinct
    machine-type codes / 8,674 total driver-pack entries from the real
    1.39MB catalog. Spot-checked machine-type `10M4` (the same one used in
    the offline fixture) -> 1 real pack, ThinkCentre M715Q, with a real
    64-hex-char `crc` value confirming the earlier finding that this field
    is actually SHA-256. Also confirmed the full-serial-truncation path
    (`10M4S00100` -> same result as `10M4`) against real catalog data. The
    referenced driver-pack download itself (`tc_m715q_w1064_201804.exe`,
    ~300MB per a `HEAD` check) was **not** downloaded to verify that hash
    end-to-end — too large to justify for a spot-check, so the SHA-256
    claim rests on the 64-character length match, not a downloaded-and-
    hashed file, for Lenovo specifically (unlike the Dell case above, where
    the actual 10.37MB file was downloaded and hashed).
    `HpPlatformCatalogSource.refresh(force=True)` run against the real
    `https://hpia.hpcloud.hp.com/ref/platformList.cab` — downloaded,
    `cabextract`-ed, and parsed in ~0.1s, indexing 603 real HP SystemIDs
    from the real 2.48MB `platformList.xml`. Spot-checked SystemID `1909`
    -> `HP ZBook 15 Mobile Workstation`, exactly matching the offline
    fixture; an unknown SystemID (`ZZZZ`) correctly returned no match.
  - **Live-network validation, Dell driver-pack catalog (2026-08-30):**
    `DellDriverPackSource.refresh(force=True)` run against the real
    `https://downloads.dell.com/catalog/DriverPackCatalog.cab` —
    downloaded, `cabextract`-ed, and parsed in ~0.4s, indexing 744 distinct
    systemIDs / 1,580 total driver-pack entries from the real 3.29MB
    `DriverPackCatalog.xml`. Spot-checked systemID `092F` (OptiPlex 5070,
    the same one used in the offline fixture) -> 2 real packs (Windows 10
    and Windows 11 variants), both with real 64-hex-char SHA-256 values;
    an unknown systemID (`ZZZZ`) correctly returned none. Unlike the
    Lenovo case above, this catalog's real SHA-256 claim *was* verified
    end-to-end: the two full-size driver packs for `092F` were each
    ~2.5GB (too large to download for a spot-check), so a different,
    much smaller real pack from the same live catalog (an 11.74MB Dell
    Latitude/OptiPlex Windows XP driver CAB) was downloaded and hashed
    with `sha256sum` independently of the code path — matched the
    catalog's published hash
    (`a64454085239c0a032292b83192ff8826ac23bf5756a6692d8433a875f11d6f2`)
    exactly. All four OEM source implementations (Dell per-device, Dell
    driver-pack, Lenovo, HP) have now had their `refresh()` pipelines run
    against live data.
- **Local signed cache** — a technician-built, content-addressed local
  store (drivers keyed by SHA-256, not filename/folder convention) — opt-in,
  incremental, no forced 20–60 GB blob.
- No default reliance on BitTorrent or any single third-party aggregator —
  avoids the AUP problem SDI has in managed enterprise networks
  ([Ed Tittel](https://www.edtittel.com/blog/working-sdio-driver-updates.html)).
- Every candidate's SHA-256 is pinned in the manifest *before* download and
  verified after; signature chain is validated against the Windows
  certificate store, not just an `sigverif` afterthought.

### 3.3 Safety & Control
- **Mandatory, verified restore point** before any install batch — the
  operation blocks and warns if the restore point creation fails (SDI's
  ticket #108 case: silent restore-point failure with no way back).
- **Backup before replace**: before installing over an existing driver,
  Waypoint exports the currently-bound driver package
  (`pnputil /export-driver`) to a local versioned backup, independent of
  System Restore.
- **Dry-run mode by default in CLI/automation contexts** — prints the exact
  `pnputil`/DISM commands and the diff, applies nothing, until `--apply` (or
  GUI "Install") is explicit.
- **Signature policy gate**: default policy blocks unsigned and test-signed
  drivers; WHQL/attestation-signed only unless a user explicitly overrides
  per-install with a logged justification.
- **No bundled vendor "helper" software** — Waypoint installs only the
  INF/CAT/driver payload via `pnputil /add-driver ... /install`
  ([Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples)),
  discarding OEM toolbars/updater bloat that DriverPack-style tools carry.
- **One-command rollback** restores the previous driver package from the
  local backup, scoped to the specific device — no dependency on whether
  System Restore succeeded.

### 3.4 Architecture & Extensibility
Layered, testable, scriptable — designed to be dropped into an IT toolchain
(RMM scripts, PDQ Deploy, Intune proactive remediation, imaging pipelines),
not just double-clicked by one technician at a time.

```
dotnet/                 # the .NET solution (Waypoint.sln)
  Waypoint.Core/        # pure logic: models, HWID matching, candidate ranking,
                        # and the IDriverSource / IModelDriverPackSource
                        # contracts — no I/O side effects, fully unit-testable
  Waypoint.Sources/     # IDriverSource implementations: LocalCacheSource,
                        # WindowsUpdateCatalogSource (AOT-blocked, see §3.2),
                        # and Oem/ (DellCatalogSource, DellDriverPackSource,
                        # LenovoDriverPackSource, HpPlatformCatalogSource,
                        # CabExtractor)
  Waypoint.Engine/      # orchestration: scan -> plan -> backup -> install ->
                        # verify -> rollback. EngineFactory, AuditLog. Owns
                        # all side effects.
  Waypoint.Platform/    # IDeviceBackend behind one interface: MockDeviceBackend
                        # and WindowsDeviceBackend (Windows/ — CfgMgr32 P/Invoke,
                        # driver signature, restore point, SMBIOS). Windows-first,
                        # no Linux backend (see §4 and ADR-0001).
  Waypoint.Cli/         # scriptable entry point: scan / plan / apply /
                        # driverpack, JSON in/out, contract exit codes,
                        # --dry-run default
  Waypoint.Gui/         # WPF presentation layer, consumes engine + core only
  *.Tests/              # xUnit, incl. real trimmed vendor catalog fixtures
  packaging/            # WiX installer + build script
docs/                   # this file, ADRs, INSTALL, TODO
```

- **Structured audit log** (JSON Lines, append-only) of every scan, plan,
  and action — required for any real IT toolchain integration and for
  post-incident review (something SDI has no equivalent of).
- **Typed JSON models, not ad-hoc index files** — candidates and session
  plans are strongly-typed records serialized through System.Text.Json
  (`Waypoint.Core/Json.cs`, `Waypoint.Cli/CliJson.cs`), and the local cache is
  a content-addressed `manifest.json` (a list of `DriverCandidate` records
  keyed by SHA-256, `Waypoint.Sources/LocalCacheSource.cs`) rather than a
  filename/folder convention. A published, schema-validated on-disk format is
  a design goal, not yet a guarantee.
- **CLI-first automation surface**: `waypoint scan --json`,
  `waypoint plan --json`, `waypoint apply` (dry-run by default, `--apply` to
  act), `waypoint driverpack --json`, with well-defined exit codes
  (0 = clean, 1 = action needed, 2 = error) so it slots into scripts and RMM
  tooling without scraping GUI output. Plan-file replay (`apply` from a saved
  plan) is out of scope — a plan on disk carries no live `Device` handles to
  install onto (see `TODO.md`).
- **GUI is a client of the engine**, not the source of truth — the same
  engine call path backs both the GUI button and the CLI command, so
  behavior can't drift between "what the button does" and "what the script
  does" (a real gap in SDI, where automation flags like `-autoinstall` behave
  differently from the interactive flow).

## 4. Platform & Stack

> **Implementation: C# / .NET 10, Windows-first.** Waypoint began as Python
> 3.12 and was reimplemented in C# / .NET; that reimplementation is now the
> product and lives in `dotnet/` on `main`. The priority behind the change was
> a small, signable, AV-clean native binary — a trust requirement, not a
> cosmetic one, for a driver tool (see §1). The original Python tree is
> preserved, unmaintained, on the `python-reference` branch; it remains the
> only working Windows Update Catalog source until that is ported (see §3.2
> and ADR-0003). Decision records:
> [`ADR-0001`](ADR-0001-language-migration-python-to-dotnet.md) (language
> choice, alternatives, and the deliberate behavioural divergences from the
> Python) and [`ADR-0002`](ADR-0002-dotnet8-to-dotnet10.md) (.NET 8 → 10). The
> stack below reflects what ships today.

- **Language:** C# / .NET 10 for `core`/`engine`/`sources`/`cli`. Windows-first.
  (.NET 8 until 2026-09-29, see [`ADR-0002`](ADR-0002-dotnet8-to-dotnet10.md).)
  Core + CLI are Native-AOT-friendly (small, dependency-free native exe for
  the automation use case).
- **GUI:** WPF, deployed self-contained + trimmed + single-file (one signable
  exe, no runtime install). GUI-first per requirements. Windows-only, matching
  the real product target. (WPF is not Native-AOT-compatible today, so the GUI
  bundles the runtime rather than being a pure AOT image; still far ahead of
  the prior PyInstaller path on size, startup, and AV reputation.)
- **Windows device layer:** CfgMgr32 (Configuration Manager) P/Invoke for the
  whole device-tree enumeration and the installed-driver / signature reads —
  deliberately **not** WMI / `System.Management`, which is neither trim- nor
  AOT-safe and is far slower (a bulk `Win32_PnPSignedDriver` query alone costs
  ~2.4s, where CfgMgr32 does the full tree in well under a second). SMBIOS
  model data is read via `GetSystemFirmwareTable`, also to stay AOT-clean. See
  `TODO.md` ("Windows device backend") for the real-hardware validation record.
- **Windows install/backup primitives:** `pnputil /add-driver ... /install`,
  `pnputil /export-driver`, `pnputil /enum-drivers`
  ([Microsoft Learn — PnPUtil syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)).
  DISM (`/Add-Driver`, offline image) is used only for offline-image /
  imaging-pipeline scenarios, not live-machine installs
  ([Microsoft Learn — DISM driver servicing](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14?view=windows-11)).
- **Cross-platform core:** the OS-specific device layer sits behind one
  interface, keeping `core`/`engine` OS-agnostic. The Python build carried a
  Linux parity backend (`pyudev` + `lspci`/`lsusb`); the .NET rewrite is
  Windows-first and drops it (see ADR-0001).
- **Packaging:** Authenticode-signed self-contained .NET binaries — a native
  CLI exe for automation and a single-file WPF exe for the GUI (both "portable
  tool" friendly, matching SDI-world expectations, without the PyInstaller
  AV-reputation cost).

## 5. Non-Goals (v1)

- No bundled offline mega-archive of drivers — sourcing is on-demand and
  pluggable from day one.
- No P2P/torrent distribution.
- No auto-apply of "upgrade available" tier without explicit user/script
  confirmation.
- No support for installing unsigned drivers without an explicit,
  logged policy override.

## 6. Open Decisions / To Revisit

### Decision records (ADRs)

Significant decisions are captured as numbered ADRs under `docs/`. This
section tracks the still-open questions; the ADRs record the settled ones.

- [`ADR-0001`](ADR-0001-language-migration-python-to-dotnet.md) — Migrate
  implementation language Python → C# / .NET (Accepted, 2026-09-04).
- [`ADR-0002`](ADR-0002-dotnet8-to-dotnet10.md) — Move runtime .NET 8 → 10 for
  LTS support (Accepted, 2026-09-30).
- [`ADR-0003`](ADR-0003-driver-sourcing.md) — Driver-sourcing strategy:
  Windows Update + a daily signed metadata index, no hosted binaries
  (**Proposed**, 2026-09-30).

### Open questions

- ~~Exact OEM catalogs to integrate first~~ — RESOLVED (2026-08-30): Dell's
  per-device `CatalogPC.cab` is genuinely HWID-keyed and implemented as
  a full `IDriverSource`; Dell/Lenovo driver-pack catalogs are model-keyed
  and implemented via the `IModelDriverPackSource` interface; HP's real
  per-update feed is WSUS SDP/WQL-based and was judged too complex to fake
  honestly, so HP support is scoped to platform-list lookup only. See
  section 3.2 for details and citations.
- Whether/how to expose HP per-device driver matching if a future WQL
  evaluator is judged worth building — not attempted this pass.
- Whether to add a scheduled/background catalog refresh for the OEM
  sources (currently caller-triggered only).
- ~~GUI settings toggle for the OEM sources~~ — RESOLVED (2026-08-30, carried
  into the .NET GUI): `Waypoint.Gui/MainWindow.xaml.cs` has an "Include OEM
  catalogs" checkbox, off by default, run on the background scan task
  (`ScanRunner`). See section 3.2 for details.
- ~~Whether a `--force-oem-refresh` (or similar) CLI flag is worth
  adding~~ — RESOLVED (2026-08-30): added. See section 3.2 for details
  (CLI `--force-oem-refresh` and the GUI's "Force refresh (ignore cached
  catalog)" checkbox, both no-ops without `--oem`/the OEM checkbox).
- ~~Implementation language~~ — RESOLVED and DONE: reimplemented Python →
  C# / .NET, Windows-first, WPF GUI, signed self-contained binaries, driven by
  a "trust/clean-binary is the top priority" call for a driver tool. The .NET
  tree is now the product (`main`); Python is preserved unmaintained on
  `python-reference`. Rationale, alternatives (Rust/Go/C++/stay-on-Python) and
  the deliberate behavioural divergences:
  [`ADR-0001`](ADR-0001-language-migration-python-to-dotnet.md) (language,
  2026-09-04). Runtime later moved .NET 8 → 10 for LTS support:
  [`ADR-0002`](ADR-0002-dotnet8-to-dotnet10.md) (2026-09-29).
- Linux device-layer scope is moot under the .NET rewrite: the Windows-first
  target drops the Linux parity backend (was `platform/linux.py` in the Python
  tree). Revisit only if a genuine cross-platform requirement returns.
- ~~Distribution channel for the compiled Windows binary~~ — RESOLVED
  (2026-09-06): ship **both** shapes from one build
  (`dotnet/packaging/build-package.ps1`), matching how SDI-world tools are
  actually used — a portable exe a technician carries on a USB stick, and a
  per-machine installer for permanent workstations.
  - **Portable zip** — unzip, run `waypoint.exe`. Nothing installed, nothing
    on PATH, no elevation.
  - **MSI (WiX)** — per-machine install to `C:\Program Files\Waypoint`,
    added to the system PATH, removable from Add/Remove Programs. MSI
    deliberately over an `.exe` installer because Intune/SCCM/GPO/PDQ
    consume it natively, which is the point for the MSP/sysadmin audience in
    section 2. Silent install/uninstall supported.
  - Both artifacts are Authenticode-signed. No background service or
    scheduled task: Waypoint runs when invoked. A scheduled catalog refresh
    remains the separate open question above.
  - State lives in `C:\ProgramData\Waypoint` for both shapes, and uninstall
    leaves it, so a reinstall keeps the technician's driver cache.
  - WiX is pinned to v5: v6+ requires accepting the Open Source Maintenance
    Fee EULA, which is a licensing decision rather than a technical one.

### Target installed layout

Naming (settled 2026-09-06): `waypoint.exe` is the CLI, `waypoint-desktop.exe`
is the GUI, and `waypoint-installer-*` is whatever ships them onto a machine —
the format is an implementation detail of delivery, not part of the identity.

```
C:\Program Files\Waypoint\
  waypoint.exe            CLI, Native AOT                ~3 MB   (on PATH)
  waypoint-desktop.exe    GUI, WPF self-contained        ~30 MB  (Start Menu)
  LICENSE.txt

C:\ProgramData\Waypoint\                                 survives uninstall
  cache\
    manifest.json                                        content-addressed index
    blobs\<sha256>\...                                   vetted driver packages
    CatalogPC.xml, DriverPackCatalog.xml,                OEM catalogs, opt-in
    catalogv2.xml, platformList.xml
  backups\<instance-id>\<timestamp>\                     pnputil /export-driver
  audit.jsonl                                            append-only JSON Lines

Start Menu\Programs\Waypoint Driver Manager\
  Waypoint Driver Manager.lnk  ->  waypoint-desktop.exe
```

Two binaries rather than one because the CLI is Native-AOT and the GUI cannot
be (WPF), so they are separately deployed — the same split 7-Zip ships
(`7z.exe` / `7zFM.exe`), and it preserves the Python entry-point names.

Measured against comparable installed tools on a real Windows 11 machine
(2026-09-06): HWiNFO64 is 6 files / 13.7 MB, 7-Zip 107 files / 5.6 MB, and
Intel Driver & Support Assistant — the closest functional analogue — 142 files
/ 20.7 MB **plus two always-running services**. Waypoint targets the lean end:
a handful of files and no resident service, which single-file/AOT publishing
makes achievable for a .NET application.
