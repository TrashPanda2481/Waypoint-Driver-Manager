# Waypoint Driver Manager

A driver detection, sourcing, and install manager for Windows, built to fix the
structural problems in tools like Snappy Driver Installer rather than give them
a new coat of paint. CLI-first for IT-toolchain automation; GUI-first for the
technician, once the GUI exists.

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
- Ships as a signed MSI or portable zip. Native AOT, no runtime install.

**Not working yet:**

- **No driver sources for a generic machine.** Every device reports
  `candidate_count: 0` unless you are on Dell/Lenovo/HP with `--oem`, or you
  populate the local cache yourself. Open question, not an oversight.
- **No GUI.** The WPF project is an empty window.
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
  Waypoint.Gui/        # WPF, not started
  *.Tests/             # xUnit, incl. real trimmed vendor catalog fixtures
  packaging/           # WiX installer + build script
docs/                  # architecture, ADR, install, TODO
```

## Build

Needs the .NET 8 SDK (or 9 — it targets `net8.0`).

```powershell
cd dotnet
dotnet test                                 # 108 tests
dotnet run --project Waypoint.Cli -- scan
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

Produces both shapes into `dotnet/packaging/artifacts/`:

| Artifact | Use |
|---|---|
| `waypoint-portable-<ver>-<rid>.zip` | Unzip and run. Nothing installed. |
| `waypoint-installer-<ver>-<rid>.msi` | Per-machine install, on PATH, Start Menu, Add/Remove Programs, silent install for Intune/SCCM/GPO/PDQ. |

Naming: `waypoint.exe` is the CLI, `waypoint-desktop.exe` will be the GUI, and
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

Signatures are SHA-256 and RFC3161-timestamped. Signing hooks AOT's
`CopyNativeBinary` step rather than `Publish`: that target replaces the apphost
in the publish directory, so signing on `Publish` is silently overwritten.

The development certificate proves the pipeline, not distribution trust — the
chain reports `UntrustedRoot`. Moving to a real OV/EV certificate is a
thumbprint change.

## The Python implementation

Waypoint began as Python 3.12 and was reimplemented in C#/.NET 8 for a small,
signable, AV-clean native binary. Rationale, alternatives and the phased plan
are in [`ADR-0001`](docs/ADR-0001-language-migration-python-to-dotnet.md),
including the deliberate behavioural divergences from the original.

The Python tree is preserved on the **`python-reference`** branch. It remains
the only working GUI and the only working Windows Update Catalog source, so it
stays as a porting reference until those land. It is not maintained.
