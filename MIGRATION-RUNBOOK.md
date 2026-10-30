# System Migration Runbook — Windows → Meridian (Linux)

> **Purpose.** A self-contained plan to wipe this machine and rebuild it with **Meridian Linux as the sole bare-metal OS**, keeping everything still needed via a Windows VM, Linux-native apps, and re-downloads. Written to be picked up by a fresh Claude on the new Linux system — hand it this file and it is caught up.
>
> **Status:** backups COMPLETE and VERIFIED; hardware test PASSED; full wipe/install scheduled for the weekend (dynamic→basic disk conversion happening first).
> **Last updated:** 2026-10-27
>
> **Verified state (2026-10-27):**
> - Meridian dev VMs → `F:\VM` — **SHA-256 byte-for-byte verified** (75.8 GB + 12.5 GB disks match).
> - `E:\DesktopArchive` (trimmed 7 GB → 1.05 GB) → `F:\DesktopArchive` — verified.
> - Docs / Pictures / VStudio (both repos) → H: stick; code also on GitHub.
> - Passwords → KeePassXC `.kdbx` (Chrome export imported, plaintext wiped).
> - Claude continuity bundle (this file, transcript, memory, skills, settings, marketplaces) → I: "Claude Backup".
> - Meridian installer: b6 on a 14 GB stick (confirmed newest functional build).
> - **Hardware test on b6 live boot: dual monitors ✓, ethernet ✓, 8851BE Wi-Fi ✓.** Only errors were cosmetic Plymouth splash rendering.
> - Dynamic span `E:` = Disk 0 (MKNSSDEL 512 GB) + Disk 2 (WDC SN730 **NVMe**); being reverted to basic. Windows C:/D: are on Disk 1 (Samsung 870 EVO, basic) — untouched.

---

## 1. Decisions locked

| Decision | Choice |
|---|---|
| End state | **Meridian Linux only on bare metal** (no dual-boot) |
| Windows | Lives in a **VirtualBox VM** for Waypoint builds, National Instruments, Weblink (automotive), DMR radio CPS, Office if needed |
| Audio | **Reaper on Linux** (native). Cubase/Steinberg **dropped** — keep only one drum track file (see §9 TODO) |
| Games | **Re-download** via Steam + Proton. Back up nothing (saves are Steam-cloud) |
| Primary OS drive | **NVMe (WDC SN730)** — Meridian root + home |
| Approach | Full clean wipe (system is years of accreted rebuilds) |

## 2. Hardware reference

- **Board:** Gigabyte B760M Gaming Plus WiFi DDR4, BIOS **F7**, UEFI/GPT. **Both M.2 slots are NVMe-only** (an M.2 *SATA* drive needs the ordered M.2-SATA→2.5"-SATA adapter).
- **CPU:** Intel i5-14400 · **RAM:** 48 GB
- **GPU:** NVIDIA RTX 3060 → install proprietary `nvidia-driver` on Linux (nouveau only for first boot)
- **Ethernet:** Realtek GbE → works out of the box (use it for install/updates)
- **Wi-Fi:** Realtek **8851BE** (Wi-Fi 6) → **CONFIRMED working on the b6 live boot** (2026-10-27). Needs kernel ≥ ~6.8 + current `linux-firmware`; ethernet also works as fallback.

## 3. Target disk layout (final)

| Physical drive | Interface | Role | Filesystem |
|---|---|---|---|
| WDC SN730 **NVMe** 477 GB | M.2 | **Meridian** (root + home) | ext4 / btrfs |
| Samsung 870 EVO 1 TB | SATA | **VM store** (Windows VM + Meridian dev VMs) + working data | btrfs (snapshots) |
| MKNSSDEL 1 TB (M.2 SATA) | **SATA via adapter** | Media / bulk data | ext4 |
| MKNSSDEL 512 GB | SATA | Backups / snapshot target | ext4 / btrfs |

> The current `E:` **dynamic spanned volume** (across the 512 + NVMe) is destroyed by the wipe. That is fine — it held only the Steam library (re-downloadable).

## 4. Backup set — what to save, what to skip

**The safe drive during the wipe is the external 1 TB (`F:`), which stays USB-connected and untouched.** Internalize it via the adapter **after** Meridian is up and data is restored.

### Back up (irreplaceable — ~150 GB)
- [ ] **Meridian dev VMs** — `D:\VM\` (`.vdi` + `.vbox`), ~90 GB — crown jewels, not in git
- [ ] **Personal data** — `Documents`, `Pictures`, `Desktop` (~40 GB; trim `Downloads` first)
- [ ] **`E:\DesktopArchive`** — 7 GB, review and keep what matters
- [ ] **Drum track / Self Image project** — back up the whole `D:\Steinberg\Projects\Audio\` folder (~94 MB): two `Drums 85BPM.wav` (31 MB June + newer 62 MB July in `Audio\Audio\`), the `Self Image.cpr`, peak cache and autosave. Drop the rest of `D:\Steinberg`.
- [ ] **Password vault** — `.kdbx` from §6
- [ ] Browser bookmarks/passwords via sync (see §6); SSH keys (`%USERPROFILE%\.ssh`); any app configs you care about
- [ ] **Program list** — `installed-programs.json` / `winget-programs.json` (already generated)

### Skip (re-acquirable — do NOT back up)
- Steam library (~518 GB) — re-download via Proton
- All installers for anything with a Linux package or a re-download
- Windows drivers, VC++ redistributables, .NET runtimes/SDKs, Windows SDKs, vendor utilities (NVIDIA/Intel/Realtek/GIGABYTE/Dell)
- Cubase/Steinberg sample content (except the one drum file)

### Already safe
- **Waypoint repo** — fully pushed to GitHub (`main`, `python-reference`; `dotnet10-migration` is already in `main`). Nothing local-only. ✅

> **Second copy:** during the wipe window the external 1 TB is the *only* untouched local drive. Put a **second copy of the crown jewels** (Meridian VMs, drum file, personal docs/photos) on cloud or another USB stick. Verify every backup (open files / checksums) **before** wiping.

## 5. Program plan (from the 144 installed)

- **Reinstall native on Linux (apt/Flatpak):** Git, GitHub CLI, VS Code, **.NET SDK** (Microsoft apt repo), CMake, Python, Rust, NVM, QEMU/KVM, **VirtualBox**, Android Studio, Arduino IDE, Chrome, Firefox, VLC, OBS, LibreOffice, Discord, Element, Zoom, balenaEtcher, Raspberry Pi Imager, 7-Zip, FFmpeg, CHIRP, LM Studio, TigerVNC, **Reaper**. (FTDI/Cypress USB-serial = built into the kernel.)
- **Windows VM only:** Visual Studio 2022 (or use Rider/VS Code on Linux), **Waypoint**, National Instruments (VIPM/VISA/IVI), Weblink (automotive), DMR radio CPS, iCloud-for-Outlook, Microsoft 365 (or web/LibreOffice).
- **Peripherals on Linux:** RGB → OpenRGB; mice/keebs → Solaar/libratbag/ckb-next (partial).
- **Games:** Steam + Proton.

## 6. Passwords & passkeys (you handle the credentials — never share them)

**Passwords (exportable):**
1. Install **KeePassXC** (Windows now, Linux later — same `.kdbx`, cross-platform; also stores passkeys going forward).
2. Export saved passwords from each browser you use — you have **Chrome, Firefox, Edge** — to CSV, then **import into KeePassXC**; set a strong master password.
3. Save the `.kdbx` to the USB/safe drive (+ second copy). Then **securely delete the plaintext CSVs**.
4. Backstop: browser **sync** restores passwords on sign-in on Linux — but keep the offline `.kdbx` regardless.

**Passkeys (mostly NOT exportable — important):**
- Windows Hello / TPM passkeys are **device-bound; they cannot be copied to a USB, and the wipe destroys them.** By design.
- Open **Settings → Accounts → Passkey settings** (Win11 25H2) to list which accounts have a device passkey. For **each one, before wiping**, confirm another way in: the account password, a passkey on your phone/security key, or saved recovery codes.
- Passkeys held in a password manager (Bitwarden/1Password) or synced via your Google account survive. On the new system, register future passkeys in a portable store (KeePassXC/Bitwarden/YubiKey) so you're never device-locked again.

**From your actual credential store:**
- Two Microsoft accounts (`helosc81@`, `stereoguy05@`) — confirm you know both passwords / have recovery (Windows Hello won't carry over; you need these to re-add accounts + Office/OneDrive on the fresh Windows).
- **GitHub (TrashPanda2481)** — re-auth with `gh auth login` on Linux; have your **2FA device/recovery codes** ready (that repo is your code backup — don't get locked out).
- The rest (OneDrive cache, Outlook push keys, Steinberg, SSO tokens) regenerates on re-login — nothing to save.

## 7. Migration sequence (ordered — do not reorder)

1. **Install KeePassXC, build the vault** (§6); back up browser bookmarks/sync.
2. **Stage the backup set** (§4) to the external 1 TB + second copy. **Verify.**
3. **Snapshot Windows** per §8 decision (capture to VHD, or skip for a fresh VM).
4. **Final verify** everything irreplaceable is in ≥1 safe place off the internal disks.
5. ~~Make the Meridian install USB + live-boot test~~ — **DONE (2026-10-27):** b6 on a 14 GB stick; live-boot confirmed dual monitors, ethernet, and 8851BE Wi-Fi all working on real hardware. Only cosmetic Plymouth splash errors.
6. **BIOS:** disable Secure Boot, confirm AHCI, set NVMe first in boot order.
7. **Wipe** all internal disks (`diskpart clean` resets the dynamic-disk metadata on the span).
8. **Install Meridian** to the NVMe (whole disk; ext4/btrfs; swapfile ok with 48 GB RAM).
9. **Post-install Meridian** (§ below), then restore data and set up VMs.
10. **Internalize the 1 TB** via the adapter once data is off it; format ext4 as the media drive.
> Note: a Windows *recovery drive* only reinstalls Windows — it does not back up files/programs, and it is not needed for the Windows VM (that's disk2vhd or an ISO).

## 8. Windows-in-VM: **DECIDED — fresh install**
Build a clean Windows in VirtualBox from a Microsoft Windows 11 ISO (no disk2vhd capture). Reinstall only the Windows-only tools (Visual Studio/Waypoint, NI stack, Weblink, DMR CPS). Smaller, cruft-free. Saves the ~300 GB VHD from the backup set.

## 9. Post-install Meridian setup
- [ ] `nvidia-driver` (RTX 3060); if Secure Boot ever re-enabled, sign the module or keep it off
- [ ] Wi-Fi 8851BE: update kernel + `linux-firmware`; use ethernet until it works
- [ ] Dev toolchain: .NET SDK (MS apt repo), Git, gh, VS Code, Python, Rust, Node (nvm), CMake
- [ ] **VirtualBox** → import the Windows VM (§8) + restore the Meridian dev VMs from `D:\VM` backup
- [ ] **Reaper** + PipeWire/JACK; test USB audio gear (Line 6 / Mooer — likely class-compliant)
- [ ] Restore `/home` personalization from the Meridian dev (Plasma) VM
- [ ] Steam + Proton; re-download games
- [ ] Mount the 1 TB media drive and the 512 backup drive

## 10. Continuity to the new system (your "persist across a new Claude" requirement)
- Copy **this file** (`MIGRATION-RUNBOOK.md`) to the safe drive **and** optionally commit it to a private GitHub repo → on Linux, `git clone` (or mount the drive), start Claude Code in that folder, and hand it this file.
- Optional: copy the Claude memory folder `C:\Users\CSaw\.claude\projects\...\memory\` to the safe drive (note: the project path differs on Linux, so this file — not memory — is the real continuity).

## TODO / open items
- [x] Drum track located: `D:\Steinberg\Projects\Audio\Drums 85BPM.wav` (recorded in §4).
- [x] §8 decided: fresh Windows in the VM (no VHD capture).
- [ ] Trim `Downloads` before backup.
