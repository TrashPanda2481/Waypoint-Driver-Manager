//! Standard on-disk locations for the local driver cache and audit log.
//! Direct port of `paths.py`.
//!
//! Centralized here so the CLI and GUI never disagree about where state
//! lives — the same "one path, not two implementations" rule that keeps
//! scan/plan/apply behavior identical between them (see
//! docs/Architecture.md section 3.4).

use std::path::PathBuf;

fn app_data_root() -> PathBuf {
    if cfg!(target_os = "windows") {
        let base = std::env::var("PROGRAMDATA").unwrap_or_else(|_| r"C:\ProgramData".to_string());
        PathBuf::from(base).join("Waypoint")
    } else {
        dirs_home().join(".local").join("share").join("waypoint")
    }
}

/// Minimal home-dir lookup so this crate doesn't need to pull in the
/// `dirs` crate for one call — mirrors Python's `Path.home()`.
fn dirs_home() -> PathBuf {
    std::env::var("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."))
}

pub fn default_cache_dir() -> PathBuf {
    app_data_root().join("cache")
}

pub fn default_audit_log_path() -> PathBuf {
    app_data_root().join("audit.jsonl")
}

pub fn default_backup_dir() -> PathBuf {
    app_data_root().join("backups")
}
