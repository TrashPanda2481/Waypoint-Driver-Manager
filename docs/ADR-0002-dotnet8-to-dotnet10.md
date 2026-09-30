# ADR-0002: Move from .NET 8 to .NET 10

- **Status:** Accepted (2026-09-30). Windows CI passed on PR #1; the packaged build was installed and run on Windows 11 and shipped as v0.2.1-alpha.1.
- **Amends:** [ADR-0001](ADR-0001-language-migration-python-to-dotnet.md), which chose C# / .NET 8. The language and design choices there stand; only the runtime version changes.

## Context

Microsoft ends support for .NET 8 (LTS) and .NET 9 (STS) on 2026-11-10. After
that there are no security fixes for either runtime. That matters twice here:
the bundled-runtime builds ship the .NET runtime, and `waypoint.exe` is Native
AOT, so the runtime is compiled into it. Neither gets patched without a
rebuild on a supported version.

.NET 10 is the current LTS, supported until 2028-11-14. .NET 11 is a release
candidate and an STS release, so it is not a candidate.

Source: the official release index,
https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json,
and https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/

## Decision

Retarget every project from `net8.0` / `net8.0-windows` to `net10.0` /
`net10.0-windows`. CI installs `10.0.x`. The framework-dependent packages are
renamed from `requires-dotnet8` to `requires-dotnet10`, because the filename
is the only place a user learns which runtime to install.

No code changes were needed.

## Evidence

Checked on Linux with SDK 10.0.401 before deciding:

- The whole solution, WPF included (`EnableWindowsTargeting`), builds with
  `-warnaserror`: 0 warnings, 0 errors.
- The 115 cross-platform tests give the same result as on .NET 8: 110 pass,
  and the same 5 fail because `expand.exe`/`makecab.exe` do not exist on
  Linux.
- A Native AOT publish of the CLI (linux-x64) is warning-free, runs, and is
  8.2 MB against 9.6 MB on .NET 8. That is a Linux figure; the Windows size is
  whatever CI reports.
- The .NET 10 breaking-change list was checked against the code. Of the
  entries that could apply (empty Grid row/column definitions, DynamicResource
  misuse, `DllImportSearchPath.AssemblyDirectory`, System.Text.Json name
  conflicts, transitive NuGet audit), none match. The .NET 9 list was not
  reviewed.

Not checked outside Windows: the GUI running, `Waypoint.Gui.Tests`, the
win-x64 AOT binary, and the MSI/zip packaging. The CI run on this change's
pull request covers the first three; packaging is manual
(`build-package.ps1`).

## Consequences

- Users of the small builds need the .NET 10 Desktop Runtime instead of 8.
  The MSI UpgradeCode is unchanged, so installing a .NET 10 build replaces a
  .NET 8 one rather than sitting beside it.
- Contributors need the .NET 10 SDK.
- The published v0.2.0-alpha.1 release stays as it is: a .NET 8 build.
  INSTALL.md says so under its download table.
- Artifact sizes in the README are the .NET 8 figures until the next release
  is built.
