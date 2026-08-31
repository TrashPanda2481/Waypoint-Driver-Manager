//! Scriptable CLI entry point — the primary surface for IT-toolchain use
//! (RMM scripts, imaging pipelines, scheduled fleet checks). Direct port
//! of `cli/main.py`.
//!
//! Design rules (see docs/Architecture.md section 3.4):
//!   - JSON in/out for every command (`--json`), so output is parseable,
//!     not scraped from human-readable text.
//!   - `apply` defaults to dry-run; installing requires an explicit
//!     `--apply` flag.
//!   - Exit codes: 0 = clean/up to date, 1 = action needed, 2 = error.
//!
//! **Parity note:** in the Python source, `cmd_apply` is itself an honest
//! stub — it prints that CLI wiring for plan-file replay is a follow-up
//! milestone and returns EXIT_ERROR, rather than actually installing
//! anything from a `waypoint apply` invocation. This port keeps that same
//! stub rather than inventing a `--plan-file` replay flow the Python CLI
//! doesn't have yet.
//!
//! **`--oem` wiring:** direct port of `cli/main.py`'s `_build_engine()`.
//! `--oem` opts into `engine::factory::build_oem_sources()` (currently:
//! Dell's `CatalogPC.cab`) in addition to the default sources.
//! `--force-oem-refresh` only has an effect together with `--oem` — a
//! warning is printed to stderr (not an error) if it's passed alone,
//! matching Python's behavior exactly.
//!
//! **Why this is a library, not just a binary:** the Python test suite
//! (`tests/test_cli_oem_flag.py`) drives a real `main(argv)` with
//! `monkeypatch.setattr("waypoint.engine.factory.build_default_backend",
//! ...)` to inject a `MockDeviceBackend`, and `capsys` to read back
//! stdout/stderr. Rust has neither capability (no monkeypatching a free
//! function out from under compiled code, and no built-in per-call stdout
//! capture outside the test harness's own output). So every command
//! function here takes its backend and its output sink as explicit
//! parameters instead of reaching for `build_default_backend()`/
//! `println!`/`eprintln!` directly — that's the dependency-injection seam
//! `crates/cli/tests/oem_flag_tests.rs` uses to port that suite. `main.rs`
//! is a thin wrapper that supplies the real backend and real
//! stdout/stderr; nothing about its behavior changes for a real user.

use std::io::Write;

use clap::{Parser, Subcommand};

use waypoint_core::SignatureType;
use waypoint_engine::factory::{build_oem_sources, build_engine_with_backend};
use waypoint_engine::WaypointEngine;
use waypoint_platform::DeviceBackend;

pub const EXIT_CLEAN: u8 = 0;
pub const EXIT_ACTION_NEEDED: u8 = 1;
pub const EXIT_ERROR: u8 = 2;

#[derive(Parser)]
#[command(name = "waypoint", about = "Waypoint Driver Manager CLI")]
pub struct Cli {
    /// Local driver cache directory (default: waypoint_engine::paths::default_cache_dir())
    #[arg(long)]
    pub cache_dir: Option<String>,

    /// Audit log path, JSON Lines (default: waypoint_engine::paths::default_audit_log_path())
    #[arg(long)]
    pub audit_log: Option<String>,

    /// Minimum driver signature tier to allow (default: attestation)
    #[arg(long, default_value = "attestation")]
    pub min_signature: String,

    /// Opt in to OEM per-device catalog sources (currently: Dell's
    /// CatalogPC.cab) in addition to the default sources. Downloads and
    /// parses a ~57MB catalog on first use per cache_dir — not something
    /// a default scan does automatically, consistent with the user
    /// instruction to limit credit/network cost to what's actually needed.
    #[arg(long, default_value_t = false)]
    pub oem: bool,

    /// Only meaningful together with --oem. Re-downloads the OEM catalog
    /// even if a previous --oem run already cached it under cache_dir.
    /// Without this flag, a second --oem run against the same cache_dir
    /// reuses the on-disk catalog instead of re-downloading it.
    #[arg(long, default_value_t = false)]
    pub force_oem_refresh: bool,

    #[command(subcommand)]
    pub command: Command,
}

#[derive(Subcommand)]
pub enum Command {
    /// Enumerate devices and assess driver status
    Scan {
        #[arg(long, default_value_t = false)]
        json: bool,
    },
    /// Build an install plan from a scan
    Plan {
        #[arg(long, default_value_t = false)]
        json: bool,
        /// Write plan JSON to this file
        #[arg(long)]
        out: Option<String>,
    },
    /// Apply a plan (dry-run unless --apply is passed)
    Apply {
        #[arg(long, default_value_t = false)]
        apply: bool,
    },
}

/// Dependency-injection seam: builds the engine from a caller-supplied
/// backend instead of resolving one via `waypoint_platform`'s real
/// hardware enumeration. `build_engine()` (below) is just this called
/// with the real backend, so production and test code can never drift
/// apart on the `--oem`/`--force-oem-refresh` wiring logic itself.
///
/// `warn_out` receives the "--force-oem-refresh has no effect without
/// --oem" warning instead of it going straight to `eprintln!`, so tests
/// can assert on it the same way Python's tests read `capsys.err`.
pub fn build_engine_with_injected_backend(
    cli: &Cli,
    backend: Box<dyn DeviceBackend>,
    warn_out: &mut dyn Write,
) -> Result<WaypointEngine, String> {
    let min_signature = SignatureType::parse(&cli.min_signature)?;
    let mut engine = build_engine_with_backend(
        backend,
        cli.cache_dir.as_deref(),
        cli.audit_log.as_deref(),
        min_signature,
    )?;

    if cli.force_oem_refresh && !cli.oem {
        let _ = writeln!(
            warn_out,
            "warning: --force-oem-refresh has no effect without --oem \
             (no OEM sources are being used this run)"
        );
    }
    if cli.oem {
        let oem_sources = build_oem_sources(cli.cache_dir.as_deref())?;
        for source in &oem_sources {
            // force=false (default): download only if this cache_dir has
            // never been refreshed before; a second --oem run against
            // the same cache_dir reuses the on-disk catalog instead of
            // re-downloading it, consistent with the user instruction to
            // limit credit/network cost to what's actually needed.
            // --force-oem-refresh overrides this and re-downloads
            // regardless of what's already cached.
            source.refresh(cli.force_oem_refresh)?;
        }
        engine.sources.extend(oem_sources);
    }
    Ok(engine)
}

/// Real path: resolves the real hardware backend, then delegates to the
/// same wiring logic every test exercises via
/// `build_engine_with_injected_backend`.
pub fn build_engine(cli: &Cli, warn_out: &mut dyn Write) -> Result<WaypointEngine, String> {
    let backend = waypoint_engine::factory::build_default_backend()?;
    build_engine_with_injected_backend(cli, backend, warn_out)
}

pub fn cmd_scan(
    engine: &WaypointEngine,
    json: bool,
    out: &mut dyn Write,
) -> Result<u8, String> {
    let assessments = engine.scan()?;

    if json {
        let payload: Vec<serde_json::Value> = assessments
            .iter()
            .map(|a| {
                serde_json::json!({
                    "instance_id": a.device.instance_id,
                    "friendly_name": a.device.friendly_name,
                    "class_name": a.device.class_name,
                    "status": a.status.as_str(),
                    "ambiguous": a.ambiguous,
                    "candidate_count": a.candidates.len(),
                    "notes": a.notes,
                })
            })
            .collect();
        let _ = writeln!(out, "{}", serde_json::to_string_pretty(&payload).unwrap());
    } else {
        for a in &assessments {
            let flag = if a.ambiguous { " [AMBIGUOUS]" } else { "" };
            let _ = writeln!(
                out,
                "[{:17}] {:12} {}{}",
                a.status.as_str(),
                a.device.class_name,
                a.device.friendly_name,
                flag
            );
        }
    }

    let action_needed = assessments.iter().any(|a| {
        matches!(
            a.status,
            waypoint_core::DeviceStatus::Missing | waypoint_core::DeviceStatus::Problem
        )
    });
    Ok(if action_needed {
        EXIT_ACTION_NEEDED
    } else {
        EXIT_CLEAN
    })
}

pub fn cmd_plan(
    engine: &WaypointEngine,
    json: bool,
    out_path: &Option<String>,
    out: &mut dyn Write,
) -> Result<u8, String> {
    let assessments = engine.scan()?;
    let plan = engine.build_plan(&assessments)?;
    let plan_json = serde_json::to_string_pretty(&plan).map_err(|e| e.to_string())?;

    if json {
        let _ = writeln!(out, "{plan_json}");
    } else {
        for entry in &plan.entries {
            let marker = if entry.requires_confirmation {
                "CONFIRM"
            } else {
                "auto"
            };
            let candidate_desc = match &entry.chosen_candidate {
                Some(c) => format!("{} ({})", c.version, c.source_id),
                None => "none".to_string(),
            };
            let _ = writeln!(
                out,
                "[{:7}] {}: {} -> {}",
                marker, entry.friendly_name, entry.status, candidate_desc
            );
        }
    }

    if let Some(path) = out_path {
        std::fs::write(path, &plan_json).map_err(|e| e.to_string())?;
    }
    Ok(EXIT_CLEAN)
}

pub fn cmd_apply(err_out: &mut dyn Write) -> u8 {
    let _ = writeln!(
        err_out,
        "apply: requires a concrete Plan + Device wiring produced by an in-process scan (see \
         waypoint_engine::session::WaypointEngine::apply). CLI wiring for --plan-file replay is \
         a follow-up milestone; use the engine directly for now."
    );
    EXIT_ERROR
}

/// Dependency-injection seam for the whole CLI: identical to `run()`
/// except the caller supplies the backend and both output streams
/// instead of them being resolved from real hardware / real stdio. This
/// is the single function `crates/cli/tests/oem_flag_tests.rs` calls to
/// port `tests/test_cli_oem_flag.py`'s `main(argv)`-driven tests.
pub fn run_with_backend(
    cli: Cli,
    backend: Box<dyn DeviceBackend>,
    out: &mut dyn Write,
    err: &mut dyn Write,
) -> u8 {
    let result = (|| -> Result<u8, String> {
        match &cli.command {
            Command::Scan { json } => {
                let engine = build_engine_with_injected_backend(&cli, backend, err)?;
                cmd_scan(&engine, *json, out)
            }
            Command::Plan { json, out: out_path } => {
                let engine = build_engine_with_injected_backend(&cli, backend, err)?;
                cmd_plan(&engine, *json, out_path, out)
            }
            // Matches main()'s original structure: `apply` never builds an
            // engine at all (it's an honest stub, see module docs), so it
            // can't fail on backend/OEM-source errors the way scan/plan can.
            Command::Apply { .. } => Ok(cmd_apply(err)),
        }
    })();
    match result {
        Ok(code) => code,
        Err(e) => {
            let _ = writeln!(err, "error: {e}");
            EXIT_ERROR
        }
    }
}

/// Real entry point: resolves the real hardware backend, writes to real
/// stdout/stderr. `main.rs` is just this.
pub fn run(cli: Cli, out: &mut dyn Write, err: &mut dyn Write) -> u8 {
    match waypoint_engine::factory::build_default_backend() {
        Ok(backend) => run_with_backend(cli, backend, out, err),
        Err(e) => {
            let _ = writeln!(err, "error: {e}");
            EXIT_ERROR
        }
    }
}
