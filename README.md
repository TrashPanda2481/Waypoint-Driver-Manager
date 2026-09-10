# Waypoint Driver Manager

[![build](https://github.com/TrashPanda2481/Waypoint-Driver-Manager/actions/workflows/build.yml/badge.svg)](https://github.com/TrashPanda2481/Waypoint-Driver-Manager/actions/workflows/build.yml)

A driver detection, sourcing, and install manager for Windows, built to fix the
structural problems in tools like Snappy Driver Installer rather than give them
a new coat of paint. CLI-first for IT-toolchain automation, GUI-first for the
technician.

[`docs/Architecture.md`](docs/Architecture.md) maps SDI's known failure modes —
sourced from public reviews, forums and issue trackers — to specific design
decisions here.

## Status: pre-alpha

It reads your real device tree. It **cannot install a driver for you yet.**

**Working, validated on real Windows 11 hardware:**

- Device enumeration via CfgMgr32, cross-checked against `Get-PnpDevice` —
  exact set match, 0 missing, 0 extra. 233 devices in ~400ms.
- Devices sharing a hardware ID are flagged and blocked pending per-device
  confirmation. On the test machine that is 103 of 233 — the failure mode
  behind SDI's ticket #108.
- `scan` / `plan` / `apply` / `driverpack` with JSON output and contract exit codes
  (0 clean, 1 action needed, 2 error). `apply` is a dry run unless `--apply`.
- Signature policy gate, backup-before-replace, restore point required before
  a batch, append-only JSON Lines audit log.
- A window that groups devices by tier and PnP setup class, and compares the
  installed driver against the candidate field by field before anything runs.
- Ships as a signed MSI or portable zip, each in a bundled-runtime and a
  smaller `requires-dotnet8` build. `waypoint.exe` is Native AOT and needs no
  runtime in either.

**Not working yet:**

- **The window scans and inspects; it does not install.** `apply` is CLI-only.
- **No driver sources for a generic machine.** Every device reports
  `candidate_count: 0` unless you are on Dell/Lenovo/HP with `--oem`, or you
  populate the local cache yourself. Open question, not an oversight.
- **`apply --apply` has never run** against a live driver.
- **Self-signed.** SmartScreen and AV reputation are unvalidated.

Full list and the remaining cutover gates: [`docs/TODO.md`](docs/TODO.md).

## Install

Download a build from [Releases](https://github.com/TrashPanda2481/Waypoint-Driver-Manager/releases)
and follow [`docs/INSTALL.md`](docs/INSTALL.md) — it covers hash verification and
the unknown-publisher warning a self-signed build produces.

```powershell
waypoint scan                       # read the device tree
waypoint scan | findstr AMBIGUOUS   # devices sharing a hardware ID
waypoint plan                       # what it would do, and what it gates
waypoint apply                      # dry run - installs nothing
waypoint driverpack                 # vendor driver packs for this system model
waypoint driverpack --model-product "OptiPlex 5070"   # ask about another machine
```

State lives in `C:\ProgramData\Waypoint`: the local driver cache and
`audit.jsonl`.

## Layout

```
dotnet/
  Waypoint.Core/       # models, matching, shared contracts. No I/O.
  Waypoint.Sources/    # local cache + Dell/Lenovo/HP OEM catalogs
  Waypoint.Engine/     # audit log, plan, scan/plan/apply orchestration, factory
  Waypoint.Platform/   # mock + the Windows backend (CfgMgr32, pnputil)
  Waypoint.Cli/        # scan / plan / apply
  Waypoint.Gui/        # WPF window: triage tree + installed-vs-candidate card
  *.Tests/             # xUnit, incl. real trimmed vendor catalog fixtures
  packaging/           # WiX installer + build script
docs/                  # architecture, ADR, install, TODO
```

## Build

Needs the .NET 8 SDK (or 9 — it targets `net8.0`).

```powershell
cd dotnet
dotnet test                                 # 134 tests
dotnet run --project Waypoint.Cli -- scan
dotnet run --project Waypoint.Gui           # the window
```

Native AOT publish additionally needs the MSVC C++ toolchain and `vswhere.exe`
on PATH (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`):

```powershell
dotnet publish Waypoint.Cli -c Release -r win-x64   # ~7MB standalone exe
```

## Packaging

```powershell
pwsh dotnet/packaging/build-package.ps1
```

Produces four artifacts into `dotnet/packaging/artifacts/`. Portable unzips and
runs with nothing installed; the installer is per-machine, on PATH, in the Start
Menu and Add/Remove Programs, and installs silently for Intune/SCCM/GPO/PDQ.

| Artifact | Size | Needs |
|---|---|---|
| `waypoint-installer-<ver>-<rid>.msi` | 67 MB | nothing |
| `waypoint-installer-<ver>-<rid>-requires-dotnet8.msi` | 11 MB | .NET 8 Desktop Runtime |
| `waypoint-portable-<ver>-<rid>.zip` | 70 MB | nothing |
| `waypoint-portable-<ver>-<rid>-requires-dotnet8.zip` | 4 MB | same runtime |

WPF has no Native AOT story, so the GUI ships on the runtime and the filename
says whether that runtime is bundled. `waypoint.exe` is Native AOT and identical
in all four, so the command line works on a machine with no runtime and no
network — which is exactly the machine a driver tool is for.

Naming: `waypoint.exe` is the CLI, `waypoint-desktop.exe` the GUI, and
`waypoint-installer-*` is whatever carries them onto a machine.

Requires WiX 5 — v6+ demands accepting the Open Source Maintenance Fee EULA,
which is a licensing decision rather than a technical one:

```powershell
dotnet tool install --global wix --version 5.*
wix extension add --global WixToolset.UI.wixext/5.0.2
```

## Signing

One-time per machine — creates a self-signed development certificate and points
the build at it:

```powershell
pwsh dotnet/setup-dev-signing.ps1
```

After that `dotnet publish` signs automatically. Certificates are referenced by
thumbprint from `CurrentUser\My`, so no key material is stored in the repo.
Precedence is `/p:WaypointSignThumbprint` > `WAYPOINT_SIGN_THUMBPRINT` > the
generated, gitignored `Directory.Build.local.props`, so a real certificate can
override the dev one per invocation without editing anything.

Signatures are SHA-256 and RFC3161-timestamped. For the AOT CLI, signing hooks
`CopyNativeBinary` rather than `Publish`: that target replaces the apphost in
the publish directory, so signing on `Publish` is silently overwritten. The GUI
is not AOT and hooks `Publish` normally.

The development certificate proves the pipeline, not distribution trust — the
chain reports `UntrustedRoot`. Moving to a real OV/EV certificate is a
thumbprint change.

## The Python implementation

Waypoint began as Python 3.12 and was reimplemented in C#/.NET 8 for a small,
signable, AV-clean native binary. Rationale, alternatives and the phased plan
are in [`ADR-0001`](docs/ADR-0001-language-migration-python-to-dotnet.md),
including the deliberate behavioural divergences from the original.

The Python tree is preserved on the **`python-reference`** branch. It remains
the only working Windows Update Catalog source, so it stays as a porting
reference until that lands. It is not maintained.

## Contributing

Issues and pull requests are welcome. `main` is protected: it takes pull
requests, not direct pushes, and force-pushes and deletions are blocked.

If you are reporting something that could be used against a machine before
there is a fix, use a private security advisory instead of an issue. See
[`SECURITY.md`](SECURITY.md).

## Licence

GPL-3.0-or-later. See [`LICENSE`](LICENSE).

Waypoint installs drivers, and the reason it exists is that the tool people
currently reach for is closed and abandoned. Copyleft is the point: anyone who
distributes a modified version has to publish their source, so a fork cannot
become the next unmaintained binary nobody can inspect.
