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
use waypoint_engine::factory::{build_oem_model_pack_sources, build_oem_sources, build_engine_with_backend};
use waypoint_engine::WaypointEngine;
use waypoint_platform::{DeviceBackend, SystemModel};
use waypoint_sources::oem::ModelDriverPackSource;

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
    /// Look up whole-system OEM driver packs (Dell/Lenovo driver-pack
    /// bundles, HP platform support) for this machine's detected model,
    /// rather than per-device candidates. Separate from `scan`/`plan`
    /// because these sources return whole downloadable bundles keyed by
    /// system model, not per-device driver candidates -- see
    /// `waypoint_engine::factory::build_oem_model_pack_sources`.
    Driverpack {
        #[arg(long, default_value_t = false)]
        json: bool,
        /// Re-download each source's catalog even if cache_dir already
        /// has one cached. Same semantics as `--force-oem-refresh` for
        /// `scan`/`plan`.
        #[arg(long, default_value_t = false)]
        force_refresh: bool,
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

/// Dependency-injection seam for `driverpack`: identical to
/// `cmd_driverpack()` except the caller supplies the `SystemModel`
/// directly instead of it being resolved via
/// `waypoint_platform::detect_system_model()`. Mirrors
/// `build_engine_with_injected_backend`'s reasoning -- Rust can't
/// monkeypatch a free function the way the Python test suite does, so
/// tests inject the value this function actually consumes.
///
/// Partial-failure resilience: if one source's `refresh()` fails (e.g.
/// no network), a warning goes to `err` and the other sources are still
/// tried -- mirrors the existing `--force-oem-refresh`-without-`--oem`
/// warning pattern in `build_engine_with_injected_backend` above, rather
/// than aborting the whole command over one source being unreachable.
pub fn cmd_driverpack_with_model(
    model: &SystemModel,
    cache_dir: Option<&str>,
    force_refresh: bool,
    json: bool,
    out: &mut dyn Write,
    err: &mut dyn Write,
) -> Result<u8, String> {
    let (dell, lenovo, hp) = build_oem_model_pack_sources(cache_dir)?;

    let dell_packs = match dell.refresh(force_refresh) {
        Ok(()) => {
            // Dell's own KB recommends the systemID lookup first, with a
            // fallback to name-matching when systemID isn't available or
            // doesn't resolve to anything -- see dell_driverpack.rs's
            // indexing comment for the citation.
            let by_sku = model
                .sku_number
                .as_deref()
                .map(|k| dell.packs_for_model(k))
                .transpose()?
                .unwrap_or_default();
            if !by_sku.is_empty() {
                by_sku
            } else {
                model
                    .product_name
                    .as_deref()
                    .map(|k| dell.packs_for_model(k))
                    .transpose()?
                    .unwrap_or_default()
            }
        }
        Err(e) => {
            let _ = writeln!(err, "warning: Dell driver-pack refresh failed, skipping: {e}");
            Vec::new()
        }
    };

    let lenovo_packs = match lenovo.refresh(force_refresh) {
        Ok(()) => model
            .product_name
            .as_deref()
            .map(|k| lenovo.packs_for_model(k))
            .transpose()?
            .unwrap_or_default(),
        Err(e) => {
            let _ = writeln!(err, "warning: Lenovo driver-pack refresh failed, skipping: {e}");
            Vec::new()
        }
    };

    let hp_supported = match hp.refresh(force_refresh) {
        Ok(()) => model
            .baseboard_product
            .as_deref()
            .map(|k| hp.is_supported(k))
            .transpose()?
            .flatten(),
        Err(e) => {
            let _ = writeln!(err, "warning: HP platform-list refresh failed, skipping: {e}");
            None
        }
    };

    let found = !dell_packs.is_empty() || !lenovo_packs.is_empty() || hp_supported.is_some();

    if json {
        let payload = serde_json::json!({
            "model": {
                "product_name": model.product_name,
                "sku_number": model.sku_number,
                "baseboard_product": model.baseboard_product,
            },
            "dell": dell_packs.iter().map(|p| serde_json::json!({
                "model_name": p.model_name,
                "model_key": p.model_key,
                "os_label": p.os_label,
                "version": p.version,
                "url": p.url,
                "size_bytes": p.size_bytes,
            })).collect::<Vec<_>>(),
            "lenovo": lenovo_packs.iter().map(|p| serde_json::json!({
                "model_name": p.model_name,
                "model_key": p.model_key,
                "os_label": p.os_label,
                "version": p.version,
                "url": p.url,
                "size_bytes": p.size_bytes,
            })).collect::<Vec<_>>(),
            "hp_supported": hp_supported.as_ref().map(|info| serde_json::json!({
                "system_id": info.system_id,
                "product_name": info.product_name,
                "supported_os_descriptions": info.supported_os_descriptions,
            })),
        });
        let _ = writeln!(out, "{}", serde_json::to_string_pretty(&payload).unwrap());
    } else {
        let _ = writeln!(
            out,
            "model: product_name={:?} sku_number={:?} baseboard_product={:?}",
            model.product_name, model.sku_number, model.baseboard_product
        );
        if dell_packs.is_empty() {
            let _ = writeln!(out, "Dell: no driver pack found");
        } else {
            for p in &dell_packs {
                let _ = writeln!(out, "Dell: {} [{}] {} -> {}", p.model_name, p.os_label, p.version, p.url);
            }
        }
        if lenovo_packs.is_empty() {
            let _ = writeln!(out, "Lenovo: no driver pack found");
        } else {
            for p in &lenovo_packs {
                let _ = writeln!(out, "Lenovo: {} [{}] {} -> {}", p.model_name, p.os_label, p.version, p.url);
            }
        }
        match &hp_supported {
            Some(info) => {
                let _ = writeln!(
                    out,
                    "HP: {} supported ({} OS entries)",
                    info.product_name,
                    info.supported_os_descriptions.len()
                );
            }
            None => {
                let _ = writeln!(out, "HP: not found in platform list");
            }
        }
    }

    Ok(if found { EXIT_CLEAN } else { EXIT_ACTION_NEEDED })
}

/// Real path: detects the real system model, then delegates to the same
/// wiring logic every test exercises via `cmd_driverpack_with_model`.
/// `force_refresh` here is `Command::Driverpack`'s own flag, not
/// `--force-oem-refresh` (that one only applies to `--oem`'s
/// per-device Dell catalog used by `scan`/`plan`; this is a separate,
/// unrelated set of sources with its own refresh flag).
pub fn cmd_driverpack(
    cli: &Cli,
    force_refresh: bool,
    json: bool,
    out: &mut dyn Write,
    err: &mut dyn Write,
) -> Result<u8, String> {
    let model = waypoint_platform::detect_system_model()?;
    cmd_driverpack_with_model(&model, cli.cache_dir.as_deref(), force_refresh, json, out, err)
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
            // driverpack looks up whole-system model packs, not
            // per-device candidates, so it never touches the
            // `DeviceBackend` this function was handed at all -- same
            // shape as `Apply` above in that respect.
            Command::Driverpack { json, force_refresh } => {
                cmd_driverpack(&cli, *force_refresh, *json, out, err)
            }
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
