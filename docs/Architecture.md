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

```
DriverSource.search(hwids: list[str]) -> list[DriverCandidate]

DriverCandidate:
  hwid, class_guid, version, driver_date, publisher,
  signature_type (whql | attestation | test_signed | unsigned),
  sha256, size_bytes, source_id, source_url, download_uri
```

Planned sources (each independently toggle-able):
- **Windows Update Catalog** — via `Microsoft.Update.Session` COM search
  (`IsInstalled=0 and Type='Driver'`), the same official channel Windows
  Update itself uses. [Microsoft Q&A](https://learn.microsoft.com/en-ca/answers/questions/5656657/microsoft-update-catalog-searching-for-firmware-up)
- **OEM vendor catalogs** — implemented for Dell, Lenovo, and (scoped down)
  HP. Verified by downloading and inspecting each vendor's real published
  feed (2026-08-30), not assumed from documentation:
  - **Dell per-device catalog** (`sources/oem/dell_catalog.py`,
    `DellCatalogSource`) — [`CatalogPC.cab`](https://downloads.dell.com/catalog/CatalogPC.cab),
    the same feed Dell Command | Update and SCCM third-party-update
    workflows consume. Genuinely per-hardware-ID: each driver component
    lists PCI `vendorID`/`deviceID`/`subVendorID`/`subDeviceID` pairs,
    matched into the same `DriverSource.search(hwids)` interface every
    other source implements. Only MD5 is published per-component (no
    SHA-256) — `search()` honestly returns `sha256=""`, and `fetch()`
    verifies against the published MD5 before computing and returning a
    real SHA-256 of the verified bytes.
  - **Dell driver-pack catalog** (`sources/oem/dell_driverpack.py`,
    `DellDriverPackSource`) — [`DriverPackCatalog.cab`](https://downloads.dell.com/catalog/DriverPackCatalog.cab),
    keyed by Dell's SMBIOS `systemID`, not by hardware ID — a different
    shape (see `ModelDriverPackSource` below), matching how MDT/SCCM
    driver-pack injection actually works. Publishes real SHA-256 per
    package.
  - **Lenovo driver-pack catalog** (`sources/oem/lenovo_driverpack.py`,
    `LenovoDriverPackSource`) — [`catalogv2.xml`](https://download.lenovo.com/cdrt/td/catalogv2.xml),
    keyed by the 4-character machine-type prefix read from a real
    system's SMBIOS product name. Its `crc` attribute is actually a
    SHA-256 digest (64 hex chars) despite the name — reported honestly as
    `hash_algorithm="sha256"`, not taken at face value as a CRC32.
  - **HP platform support list** (`sources/oem/hp_platform.py`,
    `HpPlatformCatalogSource`) — [`platformList.cab`](https://hpia.hpcloud.hp.com/ref/platformList.cab),
    **deliberately scoped down**: only answers "is this SystemID a known
    HP platform, and for which OS versions" — not a `DriverSource` or
    `ModelDriverPackSource`. HP's actual per-update applicability feed
    ([`HpCatalogForSms.latest.cab`](https://hpia.hpcloud.hp.com/downloads/sccmcatalog/HpCatalogForSms.latest.cab))
    turned out to be a WSUS Software Distribution Package (SDP) format —
    applicability is expressed as arbitrary WQL (`bar:WmiQuery` against
    `Win32_ComputerSystem`/`Win32_BaseBoard`), not a flat HWID/model list.
    Building a WQL evaluator to fake per-device matching on top of that
    was judged out of scope and dishonest to attempt partially, so
    `hp_platform.py` explicitly does not try — see its module docstring.
  - **New protocol for model-keyed sources** (`sources/oem/model_pack.py`,
    `ModelDriverPackSource` + `DriverPack`) — Dell's and Lenovo's
    driver-pack catalogs answer "what's the one bundle for this system
    model" rather than "what candidates exist for this hardware ID",
    which doesn't fit `DriverSource`. Modeled as its own small protocol
    rather than stretching `DriverSource` to cover a shape it wasn't
    designed for.
  - Not included in `build_default_sources()` — OEM catalog refresh is
    real, sizeable network I/O (Dell's `CatalogPC.xml` alone is ~57MB
    uncompressed) that shouldn't fire on every default scan. Exposed
    instead through an explicit opt-in, `engine/factory.py`'s
    `build_oem_sources()`.
  - **CLI wiring (2026-08-30):** `waypoint --oem scan`/`plan` now calls
    `build_oem_sources()` and adds the result to the engine's source list.
    Only wires in `DellCatalogSource` (the only OEM source that
    implements the `DriverSource` shape today — see the model-keyed note
    above for why Lenovo/Dell-driverpack/HP aren't included here). The
    CLI calls `.refresh(force=False)` once per invocation by default, so
    the first `--oem` run against a given `--cache-dir` pays the ~57MB
    download but every subsequent run against the same `--cache-dir`
    reuses the on-disk `CatalogPC.xml` instead of re-downloading it.
  - **GUI wiring (2026-08-30):** `gui/app.py`'s `MainWindow` now has an
    "Include OEM catalogs" checkbox, unchecked (off) by default — the
    GUI equivalent of `--oem`. When checked, `gui/workers.py`'s
    `ScanWorker` builds and refreshes the Dell source on the background
    QThread (not the UI thread) before calling `engine.scan()`, so a
    first-time ~57MB download can't freeze the window the way it would
    if it ran synchronously from the button click. The OEM source is
    merged into `engine.sources` only for that one scan and restored
    afterward in a `finally` block, so unchecking the box before the
    next scan genuinely takes effect and repeated scans with the box
    left checked never duplicate the source. The OEM cache directory is
    inferred from whichever `LocalCacheSource` the engine already has
    (`MainWindow._oem_cache_dir()`), so GUI and CLI share one cache root
    when both are pointed at the same `--cache-dir`/default location.
  - **Force-refresh flag (2026-08-30):** CLI `--force-oem-refresh` and
    the GUI's "Force refresh (ignore cached catalog)" checkbox both call
    `.refresh(force=True)` instead of `force=False`, re-downloading the
    catalog even when a cached copy exists. Both are no-ops without
    `--oem`/the OEM checkbox also being set: the CLI prints a warning to
    stderr (`--force-oem-refresh has no effect without --oem`) and
    continues rather than erroring; the GUI checkbox is disabled
    (greyed out) and automatically unchecked whenever the OEM checkbox
    is off, so it can't be left checked in a state where it would
    silently do nothing. Routine scans/fleet checks should leave this
    off — it exists for cases where the cached catalog might be stale
    (e.g. a scheduled job that explicitly wants fresh Dell data), not as
    a default.
  - **Live-network validation (2026-08-30, manual, not part of the
    automated suite):** `DellCatalogSource.refresh(force=True)` run against
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
waypoint/
  core/        # pure logic: device enumeration abstraction, HWID matching,
               # candidate ranking — no I/O side effects, fully unit-testable
  sources/     # DriverSource plugin interface + implementations
               # (windows_update.py, local_cache.py, oem/dell_catalog.py)
               # plus the model-keyed OEM protocol under sources/oem/
               # (model_pack.py, dell_driverpack.py, lenovo_driverpack.py,
               # hp_platform.py)
  engine/      # orchestration: scan -> plan -> backup -> install -> verify
               # -> rollback. Owns all side effects and the audit log.
  platform/    # OS-specific backends (win_devices.py via SetupAPI/WMI,
               # linux_devices.py via pyudev) behind one interface
  cli/         # scriptable entry point: JSON in/out, exit codes, --dry-run
  gui/         # PySide6 presentation layer, consumes engine + core only
  docs/        # this file, ADRs, manifest schema
```

- **Structured audit log** (JSON Lines, append-only) of every scan, plan,
  and action — required for any real IT toolchain integration and for
  post-incident review (something SDI has no equivalent of).
- **Versioned manifest schema** (JSON Schema-validated) replaces ad-hoc
  index files — one documented format for what a "candidate" and a
  "session plan" look like.
- **CLI-first automation surface**: `waypoint scan --json`,
  `waypoint plan --json`, `waypoint apply --plan plan.json --dry-run`,
  well-defined exit codes (0 = clean, 1 = action needed, 2 = error) so it
  slots into scripts and RMM tooling without scraping GUI output.
- **GUI is a client of the engine**, not the source of truth — the same
  engine call path backs both the GUI button and the CLI command, so
  behavior can't drift between "what the button does" and "what the script
  does" (a real gap in SDI, where automation flags like `-autoinstall` behave
  differently from the interactive flow).

## 4. Platform & Stack

> **Language migration in progress (2026-09-04).** The stack below is the
> **target** stack. Waypoint is being reimplemented in C# / .NET 8; the
> original Python 3.12+ implementation remains authoritative and runnable
> until each module has a validated .NET replacement. Rationale, alternatives,
> and the phased migration plan are in
> [`ADR-0001-language-migration-python-to-dotnet.md`](ADR-0001-language-migration-python-to-dotnet.md).
> Priority driving the change: a small, signable, AV-clean native binary —
> a trust requirement, not a cosmetic one, for a driver tool (see §1).

- **Language:** C# / .NET 8 for `core`/`engine`/`sources`/`cli`. Windows-first.
  Core + CLI are Native-AOT-friendly (small, dependency-free native exe for
  the automation use case).
- **GUI:** WPF, deployed self-contained + trimmed + single-file (one signable
  exe, no runtime install). GUI-first per requirements. Windows-only, matching
  the real product target. (WPF is not Native-AOT-compatible today, so the GUI
  bundles the runtime rather than being a pure AOT image; still far ahead of
  the prior PyInstaller path on size, startup, and AV reputation.)
- **Windows device layer:** P/Invoke to SetupAPI/CfgMgr32 and
  `System.Management` (WMI: `Win32_PnPEntity`, `Win32_PnPSignedDriver`) —
  first-class native interop, no shim layer.
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

- ~~Exact OEM catalogs to integrate first~~ — RESOLVED (2026-08-30): Dell's
  per-device `CatalogPC.cab` is genuinely HWID-keyed and now implemented as
  a full `DriverSource`; Dell/Lenovo driver-pack catalogs are model-keyed
  and implemented via the new `ModelDriverPackSource` protocol; HP's real
  per-update feed is WSUS SDP/WQL-based and was judged too complex to fake
  honestly, so HP support is scoped to platform-list lookup only. See
  section 3.2 for details and citations.
- Whether/how to expose HP per-device driver matching if a future WQL
  evaluator is judged worth building — not attempted this pass.
- Whether to add a scheduled/background catalog refresh for the OEM
  sources (currently caller-triggered only via `.refresh()`).
- ~~GUI settings toggle for `build_oem_sources()`~~ — RESOLVED (2026-08-30):
  `gui/app.py` now has an "Include OEM catalogs" checkbox, off by
  default, wired through `gui/workers.py`'s `ScanWorker`. See section
  3.2 for details.
- ~~Whether a `--force-oem-refresh` (or similar) CLI flag is worth
  adding~~ — RESOLVED (2026-08-30): added. See section 3.2 for details
  (CLI `--force-oem-refresh` and the GUI's "Force refresh (ignore cached
  catalog)" checkbox, both no-ops without `--oem`/the OEM checkbox).
- ~~Implementation language~~ — RE-EVALUATED and CHANGED (2026-09-04):
  migrating Python → C# / .NET 8, Windows-first, WPF GUI, signed
  self-contained binaries. Driven by a "trust/clean-binary is the top
  priority" call for a driver tool. Full rationale, alternatives (Rust/Go/
  C++/stay-on-Python), and the phased migration plan in
  [`ADR-0001-language-migration-python-to-dotnet.md`](ADR-0001-language-migration-python-to-dotnet.md).
- Linux device-layer scope is moot under the .NET rewrite: the Windows-first
  target drops the Linux parity backend (was `platform/linux.py`). Revisit
  only if a genuine cross-platform requirement returns.
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
