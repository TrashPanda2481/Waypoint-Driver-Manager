//! Minimal streaming HTTP download. Port of `sources/oem/http.py`.
//!
//! Python's version is stdlib-only (`urllib.request`) because Waypoint's
//! core has zero third-party dependencies by design. Rust's standard
//! library has no HTTP client at all, so this is a genuinely new,
//! disclosed dependency for the Rust build — `ureq` (blocking, small
//! dependency tree, rustls-based, no async runtime required) rather than
//! something heavier like `reqwest`, to stay as close as possible to the
//! Python version's "just enough to download a file" scope. Like
//! `cabextract` in `cab.rs`, this is a real dependency, not something
//! silently assumed.
//!
//! `download_file` is a plain function (not injected as a trait object)
//! so each OEM source module can override it directly via a function
//! pointer field for tests — mirroring how Python's `downloader` callable
//! parameter keeps tests off the network.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::Duration;

pub const DEFAULT_TIMEOUT_SECONDS: u64 = 60;
const USER_AGENT: &str =
    "Waypoint-Driver-Manager/0.1 (+https://github.com/TrashPanda2481/Waypoint-Driver-Manager)";

/// Stream `url` to `dest_path`. Returns an error string on any failure —
/// callers decide how to surface that (CLI error message, GUI status
/// line), consistent with how `windows_update.rs` leaves error handling
/// to its caller.
pub fn download_file(url: &str, dest_path: &Path) -> Result<PathBuf, String> {
    download_file_with_timeout(url, dest_path, DEFAULT_TIMEOUT_SECONDS)
}

pub fn download_file_with_timeout(
    url: &str,
    dest_path: &Path,
    timeout_secs: u64,
) -> Result<PathBuf, String> {
    if let Some(parent) = dest_path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }

    let config = ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(timeout_secs)))
        .build();
    let agent: ureq::Agent = config.into();

    let mut response = agent
        .get(url)
        .header("User-Agent", USER_AGENT)
        .call()
        .map_err(|e| format!("GET {url} failed: {e}"))?;

    let mut reader = response.body_mut().as_reader();
    let mut out_file = fs::File::create(dest_path).map_err(|e| e.to_string())?;
    let mut buf = [0u8; 1 << 16];
    loop {
        let n = std::io::Read::read(&mut reader, &mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        out_file.write_all(&buf[..n]).map_err(|e| e.to_string())?;
    }

    Ok(dest_path.to_path_buf())
}
