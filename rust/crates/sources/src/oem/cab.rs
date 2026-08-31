//! Portable Microsoft Cabinet (.cab) extraction. Port of
//! `sources/oem/cab.py`.
//!
//! OEM catalogs (Dell, HP, Lenovo's BIOS/driver packs) are distributed as
//! .cab files. Rather than bundle a Rust CAB-parsing dependency, this
//! shells out to whichever extractor is actually available — the exact
//! same strategy the Python version uses, for the exact same reason:
//!
//!   - Windows: `expand.exe` ships with every Windows installation since
//!     Windows 2000 (<https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/expand>) —
//!     no extra install required on the platform Waypoint primarily targets.
//!   - Linux/macOS (dev/CI machines, not an install target): `cabextract`
//!     (<https://www.cabextract.org.uk/>), a common but *not universally
//!     preinstalled* package — a real, disclosed dependency for running
//!     the OEM-catalog code path outside Windows, not something Waypoint
//!     silently assumes.
//!
//! If neither is found, `extract_cab` returns an actionable error message
//! instead of failing in a confusing way deep inside catalog parsing.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

/// Extract every file in `cab_path` into `dest_dir`. Returns the list of
/// extracted file paths on success.
pub fn extract_cab(cab_path: &Path, dest_dir: &Path) -> Result<Vec<PathBuf>, String> {
    fs::create_dir_all(dest_dir).map_err(|e| e.to_string())?;

    if cfg!(target_os = "windows") {
        // expand.exe -F:* <cab> <destdir> extracts every file, preserving
        // names but not subdirectories (matches how Dell/HP/Lenovo cabs
        // are laid out: flat, single XML payload).
        run_extractor(
            "expand.exe",
            &["-F:*", &path_str(cab_path)?, &path_str(dest_dir)?],
        )?;
    } else if which("cabextract") {
        run_extractor("cabextract", &["-d", &path_str(dest_dir)?, &path_str(cab_path)?])?;
    } else {
        return Err(format!(
            "No CAB extractor available to unpack {}. On Windows this should never happen \
             (expand.exe ships with the OS) — if you see this on Windows, something is very \
             wrong with the PATH. On Linux/macOS dev machines, install cabextract, e.g. \
             `apt install cabextract` / `brew install cabextract`.",
            cab_path.display()
        ));
    }

    let mut extracted = Vec::new();
    for entry in fs::read_dir(dest_dir).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path();
        if path.is_file() {
            extracted.push(path);
        }
    }
    Ok(extracted)
}

fn run_extractor(program: &str, args: &[&str]) -> Result<(), String> {
    let output = Command::new(program)
        .args(args)
        .output()
        .map_err(|e| format!("failed to run {program}: {e}"))?;
    if !output.status.success() {
        return Err(format!(
            "{program} failed (exit {:?}): {}",
            output.status.code(),
            String::from_utf8_lossy(&output.stderr)
        ));
    }
    Ok(())
}

fn path_str(path: &Path) -> Result<String, String> {
    path.to_str()
        .map(str::to_string)
        .ok_or_else(|| format!("path is not valid UTF-8: {}", path.display()))
}

/// Minimal `shutil.which`-equivalent PATH search, avoiding a `which`
/// crate dependency for one lookup.
fn which(program: &str) -> bool {
    std::env::var_os("PATH")
        .map(|paths| {
            std::env::split_paths(&paths).any(|dir| dir.join(program).is_file())
        })
        .unwrap_or(false)
}
