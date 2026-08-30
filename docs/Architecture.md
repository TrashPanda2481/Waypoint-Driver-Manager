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
- **OEM vendor catalogs** — Dell Command, HP Image Assistant, Lenovo/Intel
  published driver feeds where they expose machine-readable catalogs.
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
               # (windows_update.py, oem_catalog.py, local_cache.py)
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

- **Language:** Python 3.12+ for `core`/`engine`/`sources`/`cli` — matches
  existing tooling fluency and keeps the logic genuinely cross-platform.
- **GUI:** PySide6 (Qt) — GUI-first per requirements, and the same toolkit
  already used for Compass GUI in Meridian OS, so patterns/QA habits carry
  over even though this is a separate product.
- **Windows device layer:** `pywin32`/WMI (`Win32_PnPEntity`,
  `Win32_PnPSignedDriver`) for the first working version; option to move to
  direct SetupAPI/CfgMgr32 via `ctypes` later if WMI enumeration proves too
  slow on large fleets.
- **Windows install/backup primitives:** `pnputil /add-driver ... /install`,
  `pnputil /export-driver`, `pnputil /enum-drivers`
  ([Microsoft Learn — PnPUtil syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)).
  DISM (`/Add-Driver`, offline image) is used only for offline-image /
  imaging-pipeline scenarios, not live-machine installs
  ([Microsoft Learn — DISM driver servicing](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14?view=windows-11)).
- **Linux device layer:** `pyudev` + `lspci`/`lsusb` fallback, mapped onto
  the same internal `Device`/`DriverCandidate` model so the core logic and
  GUI don't need to know which OS they're on.
- **Packaging:** PyInstaller one-file build for the Windows GUI/CLI binary
  (matches "portable tool" expectations from the SDI world) + a pip-installable
  package for the CLI/automation use case.

## 5. Non-Goals (v1)

- No bundled offline mega-archive of drivers — sourcing is on-demand and
  pluggable from day one.
- No P2P/torrent distribution.
- No auto-apply of "upgrade available" tier without explicit user/script
  confirmation.
- No support for installing unsigned drivers without an explicit,
  logged policy override.

## 6. Open Decisions / To Revisit

- Exact OEM catalogs to integrate first (Dell/HP/Lenovo have differing
  levels of machine-readable feed support — needs a scoping pass per vendor).
- Whether `platform/linux_devices.py` targets kernel-module/firmware
  matching (closer to a different problem domain) or stays scoped to
  Linux-side testing/parity for the core engine only.
- Distribution channel for the compiled Windows binary (GitHub Releases vs.
  a signed installer) — deferred until v1 core is functional.
