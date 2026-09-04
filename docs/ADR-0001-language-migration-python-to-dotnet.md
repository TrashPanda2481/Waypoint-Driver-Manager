# ADR-0001 — Migrate implementation language from Python to C# / .NET 8

- **Status:** Accepted (2026-09-04) — migration in progress; Python tree
  remains authoritative until each module is ported and validated.
- **Supersedes:** the "Language: Python 3.12+" decision in
  [`Architecture.md`](Architecture.md) section 4.
- **Deciders:** project owner (priority call), Claude (analysis).

## Context

Waypoint's implementation language was originally chosen as Python 3.12+
(see Architecture.md §4) for tooling fluency, a cross-platform core, and
reuse of the PySide6/Qt patterns from Meridian's Compass GUI, with
PyInstaller one-file builds for distribution.

That decision was deliberately re-opened and pressure-tested on
2026-09-04. The re-evaluation was framed around a single question: for the
tool's **shipped form**, what matters most? The owner's answer:

> **A small, signable, AV-clean native binary is the top priority.**

This is not a cosmetic preference for a driver-installation tool. Waypoint
exists to fix the *trust* problems of the SDI/SDIO/DriverPack lineage (see
Architecture.md §1). A tool that ships as an unsigned, bloated, and
routinely AV-flagged binary undermines exactly the trust it is trying to
establish.

### Where Python actually costs us

The end-user "no dependencies" concern is already solved by PyInstaller —
users get one `.exe`, no Python/Qt install required. The costs Python
imposes that **do not** go away:

1. **AV false positives.** PyInstaller-packed executables are routinely
   flagged by Windows Defender / SmartScreen. For a driver tool aimed at
   IT technicians and general users, this is an adoption blocker, not a
   nuisance.
2. **Binary bloat.** Python + Qt + pywin32 one-file builds land ~50–90 MB.
3. **Indirect Windows interop.** SetupAPI / CfgMgr32 / WMI / `pnputil` /
   driver signing are all reached through `ctypes` / `pywin32` shims —
   always a translation layer over what is fundamentally a Windows-native
   systems task.
4. **Dev-env friction.** The test suite goes partly red without the
   `pywin32` / `PySide6` extras installed (observed 2026-09-04:
   `No module named 'win32com'`, PySide6 GUI module skipped).

## Decision

**Reimplement Waypoint in C# / .NET 8**, Windows-first, with WPF for the
GUI. Distribute as signed self-contained binaries.

### Target stack

- **Core / engine / sources / CLI:** .NET 8 class libraries + a console
  app. Native-AOT-friendly, yielding a small (~5–15 MB) dependency-free
  native CLI exe for the IT-automation use case.
- **Windows device layer:** P/Invoke to SetupAPI / CfgMgr32 and
  `System.Management` (WMI: `Win32_PnPEntity`, `Win32_PnPSignedDriver`).
  Install/backup primitives stay `pnputil`-based
  (`/add-driver /install`, `/export-driver`, `/enum-drivers`).
- **GUI:** WPF, deployed **self-contained + trimmed + single-file**
  (~20–40 MB, one signable exe, no runtime install). Windows-only, which
  matches the real product target.
- **Distribution:** Authenticode-signed binaries (first-class in the .NET
  toolchain). GitHub Releases channel TBD (Architecture.md §6).

### Honest nuance

Native AOT is **not** available for WPF today, so the GUI is a
self-contained trimmed single-file deployment (bundles the runtime), not a
pure-AOT native image. This is still far ahead of PyInstaller on binary
size, startup time, and — critically — AV reputation and signability.
Native AOT is reserved for the CLI/core path.

## Alternatives considered

| Option | Verdict |
|---|---|
| **Stay on Python + code-sign** | Signing blunts but does not eliminate the PyInstaller AV-reputation problem; bloat and indirect interop remain. Rejected given the stated top priority. |
| **Rust** | Best possible binary (tiny, static, best AV trust) and excellent official `windows-rs` bindings. Rejected on GUI: no mature batteries-included native toolkit, a real productivity tax for a GUI-first, data-heavy device tree. |
| **Go** | Single static binary, but SetupAPI interop is more DIY and the GUI story is as weak as Rust's. Poor fit for GUI-first + deep driver work. |
| **C++** | The language the SetupAPI/pnputil docs target; maximum control but slowest iteration and painful GUI. Overkill without a perf driver. |
| **C# / .NET 8 (chosen)** | Native Windows citizen, first-class signing, small dependency-free binaries, native GUI (WPF), mainstream ecosystem. Best fit for the stated priority. |

## Consequences

### Positive
- Signable, AV-clean, dependency-free native binaries — the decisive win.
- Direct, first-class Windows driver interop (no shim layer).
- Native GUI toolkit; no Qt dependency to package.

### Negative / costs
- Loses the Linux parity backend (`platform/linux.py`) — acceptable, the
  product is Windows-first.
- Loses shared-Python lineage with Meridian's Compass GUI patterns.
- ~2,300 LOC reimplementation. Mitigated by porting **now**, while the
  two rewrite-heavy pieces (Windows backend, GUI) are the least-built and
  least-validated parts — the cheapest possible moment to switch.

### Timing rationale
The only unvalidated code today is exactly the Windows-native layer — the
part where Python is weakest and where a later switch would hurt most.
Every week invested in the pywin32 path raises the switching cost. Now is
the low-water mark.

## What ports cleanly vs. what is a genuine rewrite

| Current (Python) | .NET target | Effort |
|---|---|---|
| `core/` matching + models | `Waypoint.Core` class library + xUnit tests | Clean port — pure logic |
| `engine/` session, audit, factory | Same shape in C# | Clean port |
| `sources/oem/*`, `cab`, `http` | `System.IO.Packaging`/`System.Xml`, `HttpClient`; CAB via a lib or bundled tool | Mostly clean |
| `sources/local_cache`, `windows_update` | C# re-target | Clean / re-target |
| `platform/windows` (pywin32/WMI) | P/Invoke + `System.Management` | Genuine rewrite — but never validated, nothing lost |
| `platform/linux` parity backend | Dropped (Windows-first) | N/A |
| `gui/` (PySide6) | WPF | Rewrite — but it is a skeleton |
| test fixtures (real trimmed vendor catalogs) | Reused as-is | Kept verbatim |
| `docs/Architecture.md`, OEM validation notes | Reused | Kept |

## Migration plan (lowest-risk-first)

1. **Proof-of-concept slice.** Scaffold the .NET 8 solution
   (`Waypoint.Core`, `Waypoint.Cli`, `Waypoint.Gui`, test project), port
   `core/` matching + its tests, and produce a **signed self-contained CLI
   exe**. Validates the whole toolchain — including signing and AV
   behavior — on a small, verifiable slice before committing to the full
   port. The Python tree stays untouched.
2. **Port `engine/` + `sources/`** (including the OEM catalogs), reusing
   the existing real-data test fixtures.
3. **Rewrite the Windows device backend** against SetupAPI / WMI and
   **validate it on real Windows 11 hardware** — the milestone Waypoint
   was already approaching.
4. **Build the WPF GUI** on top of the shared core.

The Python implementation remains authoritative and runnable until each
module above has a validated .NET replacement.
