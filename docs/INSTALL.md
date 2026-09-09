# Install and try Waypoint

Pre-alpha. This installs and runs, and it reads your real device tree — but it
cannot install a driver for you yet. See [What works](#what-works) before you
judge it.

Windows 10 or 11, x64. No .NET runtime needed.

---

## 1. Get the build

Download from the repo's **Releases** page. The repo is private, so be signed in
to GitHub, or use the CLI:

```powershell
gh release download v0.1.0-alpha.1 --repo TrashPanda2481/Waypoint-Driver-Manager
```

Two files, pick one:

| File | Use |
|---|---|
| `waypoint-installer-0.1.0-win-x64.msi` | Installs to Program Files, adds to PATH, Start Menu entry |
| `waypoint-portable-0.1.0-win-x64.zip` | Unzip and run. Nothing installed, nothing on PATH |

## 2. Verify what you downloaded

The tool's whole premise is not trusting unverified binaries, so it would be odd
not to offer the hashes.

```powershell
Get-FileHash .\waypoint-installer-0.1.0-win-x64.msi -Algorithm SHA256
```

```
msi  b37a1befb096709097d2ccdf91e743d9fb8a362fbc17dc2be7588af81a534e71
zip  74249a88ccc2f15ffab46d42550e5a990fbd35979b821233e904d975dbf46fde
```

## 3. Expect Windows to warn you

**It will say the publisher is unknown, and SmartScreen may block it.** That
warning is correct and you should not ignore it on reflex.

The binaries are Authenticode-signed, but with a **self-signed development
certificate** — there is no trusted chain behind it, so Windows cannot vouch for
who built it. A real OV/EV certificate is pending; swapping it in is a one-line
change on our side and will make these warnings go away.

Bypass it only because you know where the file came from and the hash above
matched:

- **SmartScreen** — "Windows protected your PC" → *More info* → *Run anyway*
- **UAC** — will show *Unknown publisher*. The MSI is per-machine, so elevation
  is genuinely required.

If your AV quarantines it, that is a data point we want — please say so, with
the product name and detection name.

## 4. Install

Double-click the MSI and click through, or silently:

```powershell
msiexec /i waypoint-installer-0.1.0-win-x64.msi /qn
```

Portable instead: unzip anywhere and run `waypoint.exe` from that folder.

## 5. Open it

**Start → type "Waypoint" → Waypoint Driver Manager.** A console opens on the
command list with `waypoint` ready to use.

Or from any terminal, just `waypoint`.

> Already had a terminal open before installing? PATH changes only reach *new*
> shells. Close it and open a fresh one.

## 6. Try it

```powershell
waypoint scan                          # read the device tree
waypoint scan | findstr AMBIGUOUS      # devices sharing a hardware ID
waypoint scan --json                   # machine-readable
waypoint --min-signature whql scan     # raise the trust bar, watch drivers fail it
waypoint plan                          # what it would do, and what it gates
waypoint apply                         # dry run — installs nothing
waypoint driverpack                    # vendor driver packs for this model
```

Exit codes are contract: **0** clean, **1** action needed, **2** error.
Check with `echo %ERRORLEVEL%` (cmd) or `$LASTEXITCODE` (PowerShell).

State lives in `C:\ProgramData\Waypoint` — the local driver cache and an
append-only `audit.jsonl` of every scan and decision.

---

## What works

- Reads the live device tree through CfgMgr32. Validated against Windows'
  own `Get-PnpDevice`: exact set match, zero missing, zero extra.
- Flags devices that share a hardware ID and refuses to act on them without
  per-device confirmation.
- Signature policy gate, dry-run default, confirmation enforced by the engine.
- JSON output, documented exit codes, silent MSI install.

## What does not work yet

- **It cannot offer you a driver.** Every device reports `candidate_count: 0`
  unless you are on a Dell, Lenovo or HP machine *and* pass `--oem`, or you have
  populated the local cache yourself. It detects and gates correctly; it has
  nothing to install. This is the main open question, not an oversight.
- **No GUI.** CLI only.
- **A real install has never been performed.** `apply --apply` is untested
  against a live driver. Do not run it on a machine you care about.
- **Self-signed.** See step 3.

Full list: [`TODO.md`](TODO.md).

## Uninstall

Add/Remove Programs → Waypoint Driver Manager, or:

```powershell
msiexec /x waypoint-installer-0.1.0-win-x64.msi /qn
```

Files and the PATH entry are removed. `C:\ProgramData\Waypoint` is left in
place on purpose, so a reinstall keeps a technician's driver cache. Delete it
by hand if you want a clean slate.

---

## Building from source

```powershell
git clone https://github.com/TrashPanda2481/Waypoint-Driver-Manager.git
cd Waypoint-Driver-Manager/dotnet
dotnet test                # 108 tests
dotnet run --project Waypoint.Cli -- scan
```

Needs the .NET 8 SDK (or 9 — it targets `net8.0`).

To produce the installer yourself you also need the **MSVC C++ toolchain** for
Native AOT and **WiX 5**:

```powershell
dotnet tool install --global wix --version 5.*
wix extension add --global WixToolset.UI.wixext/5.0.2
pwsh dotnet/setup-dev-signing.ps1     # one-off: creates a local signing cert
pwsh dotnet/packaging/build-package.ps1
```

`build-package.ps1` needs `vswhere.exe` on PATH
(`C:\Program Files (x86)\Microsoft Visual Studio\Installer`); it adds it
automatically if Visual Studio is in the standard location.

Design rationale is in [`Architecture.md`](Architecture.md) and
[`ADR-0001`](ADR-0001-language-migration-python-to-dotnet.md).

The original Python implementation is preserved on the `python-reference`
branch as a porting reference. It is not maintained.
