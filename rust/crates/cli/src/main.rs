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
//! **Scope note:** `--oem` maps to `build_oem_sources()` in Python, which
//! is not ported in this pass (see `waypoint-sources` crate docs). Passing
//! `--oem` here is an honest error, not a silent no-op.

use std::process::ExitCode;

use clap::{Parser, Subcommand};

use waypoint_core::SignatureType;
use waypoint_engine::factory::build_default_engine;
use waypoint_engine::WaypointEngine;

const EXIT_CLEAN: u8 = 0;
const EXIT_ACTION_NEEDED: u8 = 1;
const EXIT_ERROR: u8 = 2;

#[derive(Parser)]
#[command(name = "waypoint", about = "Waypoint Driver Manager CLI")]
struct Cli {
    /// Local driver cache directory (default: waypoint_engine::paths::default_cache_dir())
    #[arg(long)]
    cache_dir: Option<String>,

    /// Audit log path, JSON Lines (default: waypoint_engine::paths::default_audit_log_path())
    #[arg(long)]
    audit_log: Option<String>,

    /// Minimum driver signature tier to allow (default: attestation)
    #[arg(long, default_value = "attestation")]
    min_signature: String,

    /// Opt in to OEM per-device catalog sources in addition to the default
    /// sources. NOT YET PORTED to Rust — passing this flag is a hard error,
    /// not a silent no-op, so a fleet script never assumes OEM coverage it
    /// isn't getting.
    #[arg(long, default_value_t = false)]
    oem: bool,

    /// Only meaningful together with --oem. NOT YET PORTED (see --oem).
    #[arg(long, default_value_t = false)]
    force_oem_refresh: bool,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
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

fn build_engine(cli: &Cli) -> Result<WaypointEngine, String> {
    let min_signature = SignatureType::parse(&cli.min_signature)?;
    let engine = build_default_engine(
        cli.cache_dir.as_deref(),
        cli.audit_log.as_deref(),
        min_signature,
    )?;

    if cli.force_oem_refresh && !cli.oem {
        eprintln!(
            "warning: --force-oem-refresh has no effect without --oem \
             (no OEM sources are being used this run)"
        );
    }
    if cli.oem {
        return Err(
            "--oem requires OEM catalog sources (Dell CatalogPC.cab, etc.), which are not yet \
             ported to this Rust build — see docs/TODO-rust-port.md. Run without --oem, or use \
             the Python CLI for OEM-catalog scans for now."
                .to_string(),
        );
    }
    Ok(engine)
}

fn cmd_scan(cli: &Cli, json: bool) -> Result<ExitCode, String> {
    let engine = build_engine(cli)?;
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
        println!("{}", serde_json::to_string_pretty(&payload).unwrap());
    } else {
        for a in &assessments {
            let flag = if a.ambiguous { " [AMBIGUOUS]" } else { "" };
            println!(
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
    Ok(ExitCode::from(if action_needed {
        EXIT_ACTION_NEEDED
    } else {
        EXIT_CLEAN
    }))
}

fn cmd_plan(cli: &Cli, json: bool, out: &Option<String>) -> Result<ExitCode, String> {
    let engine = build_engine(cli)?;
    let assessments = engine.scan()?;
    let plan = engine.build_plan(&assessments)?;
    let plan_json = serde_json::to_string_pretty(&plan).map_err(|e| e.to_string())?;

    if json {
        println!("{plan_json}");
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
            println!(
                "[{:7}] {}: {} -> {}",
                marker, entry.friendly_name, entry.status, candidate_desc
            );
        }
    }

    if let Some(path) = out {
        std::fs::write(path, &plan_json).map_err(|e| e.to_string())?;
    }
    Ok(ExitCode::from(EXIT_CLEAN))
}

fn cmd_apply() -> ExitCode {
    eprintln!(
        "apply: requires a concrete Plan + Device wiring produced by an in-process scan (see \
         waypoint_engine::session::WaypointEngine::apply). CLI wiring for --plan-file replay is \
         a follow-up milestone; use the engine directly for now."
    );
    ExitCode::from(EXIT_ERROR)
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let result = match &cli.command {
        Command::Scan { json } => cmd_scan(&cli, *json),
        Command::Plan { json, out } => cmd_plan(&cli, *json, out),
        Command::Apply { .. } => Ok(cmd_apply()),
    };
    match result {
        Ok(code) => code,
        Err(err) => {
            eprintln!("error: {err}");
            ExitCode::from(EXIT_ERROR)
        }
    }
}
