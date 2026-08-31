//! Waypoint GUI — iced port of `gui/app.py` (originally PySide6).
//!
//! Structured around the same three-tier triage as the Python GUI
//! (Missing / Problem / Upgrade available, see docs/Architecture.md
//! section 3.1). Scan runs the exact same `WaypointEngine::scan()` and
//! `build_default_engine()` wiring the CLI uses (see
//! `waypoint_engine::factory`), so this GUI can't silently disagree with
//! `waypoint-cli scan`.
//!
//! **Honest deviations from the Python version (documented, not hidden):**
//! - The Python GUI runs `scan()` on a background `QThread` so slow WMI
//!   enumeration or an OEM catalog download can't freeze the window (see
//!   `gui/workers.py`). Wiring that same non-blocking behavior in iced
//!   requires `Box<dyn DeviceBackend>` to be provably `Send` across an
//!   async task boundary, which the trait doesn't currently guarantee.
//!   Rather than fake responsiveness or add an unverified `unsafe impl
//!   Send`, this port runs `scan()` synchronously on button click. Local
//!   udev/mock enumeration is fast enough that this is not noticeable in
//!   practice, but a real Windows WMI fleet scan could briefly block the
//!   window — tracked as deferred scope, see docs/TODO-rust-port.md.
//! - The OEM checkbox is opt-in, off by default, and now wired the same
//!   way as the CLI's `--oem` flag: checking it before clicking Scan adds
//!   `engine::factory::build_oem_sources()` (Dell's real per-device
//!   catalog) to that one scan's source list, refreshed with
//!   `force_oem_refresh` per the force-refresh checkbox, then restored to
//!   the original source list afterward — mirroring `ScanWorker.run()`'s
//!   try/finally sources-restore pattern in `gui/workers.py` (Python runs
//!   this on a background thread; this port runs it synchronously on
//!   click, per the deviation noted above).
//! - Rendered as a grouped column list rather than Qt's `QTreeWidget`
//!   (iced has no built-in tree widget); the same three-tier grouping,
//!   counts, and per-row fields (device, class, status, candidate
//!   version, source, signature) are preserved.
//! - **Driver-pack lookup section (new scope, no Python GUI equivalent
//!   — `gui/app.py`/`gui/workers.py` never had this feature either).**
//!   Reuses `waypoint_cli::cmd_driverpack_with_model` directly rather
//!   than re-implementing the Dell-SKU-then-name-fallback/Lenovo/HP
//!   wiring a second time — same reasoning as `factory.rs`'s module
//!   doc: if CLI and GUI logic lived in two places they could silently
//!   drift apart. Runs synchronously on button click, same threading
//!   deviation as Scan above (network refresh of the Dell/Lenovo/HP
//!   catalogs could briefly block the window on a real machine with an
//!   empty cache — not exercised in this sandbox, which has no
//!   `/sys/class/dmi/id` to detect a real model from in the first
//!   place).

use iced::widget::{button, checkbox, column, container, row, scrollable, text, Column};
use iced::{Alignment, Element, Length, Task};

use waypoint_core::{DeviceAssessment, DeviceStatus};
use waypoint_engine::factory::{build_default_engine, build_oem_sources};
use waypoint_engine::WaypointEngine;

const TIER_ORDER: [DeviceStatus; 3] = [
    DeviceStatus::Missing,
    DeviceStatus::Problem,
    DeviceStatus::UpgradeAvailable,
];

fn tier_label(status: DeviceStatus) -> &'static str {
    match status {
        DeviceStatus::Missing => "Missing",
        DeviceStatus::Problem => "Problem",
        DeviceStatus::UpgradeAvailable => "Upgrade available",
        DeviceStatus::UpToDate => "Up to date",
    }
}

struct WaypointApp {
    engine: Option<WaypointEngine>,
    engine_error: Option<String>,
    backend_label: String,
    status_line: String,
    assessments: Vec<DeviceAssessment>,
    scanning: bool,
    /// GUI equivalent of the CLI's `--oem` flag — off by default, opt-in.
    include_oem: bool,
    force_oem_refresh: bool,
    /// GUI equivalent of `waypoint driverpack --force-refresh`.
    driverpack_force_refresh: bool,
    driverpack_running: bool,
    driverpack_status: String,
    /// Raw parsed JSON from `cmd_driverpack_with_model`'s `--json` output —
    /// kept as `serde_json::Value` rather than typed structs since this is
    /// display-only and the shape is already documented at the CLI layer
    /// (`crates/cli/src/lib.rs`'s `Driverpack` doc comment).
    driverpack_result: Option<serde_json::Value>,
}

#[derive(Debug, Clone)]
enum Message {
    ToggleOem(bool),
    ToggleForceOemRefresh(bool),
    Scan,
    ToggleDriverpackForceRefresh(bool),
    LookupDriverpacks,
}

impl WaypointApp {
    fn new() -> (Self, Task<Message>) {
        let (engine, engine_error, backend_label) = match build_default_engine(None, None, waypoint_core::SignatureType::Attestation) {
            Ok(engine) => {
                let label = engine.backend.name().to_string();
                (Some(engine), None, label)
            }
            Err(err) => (None, Some(err), "unavailable".to_string()),
        };

        (
            Self {
                engine,
                engine_error,
                backend_label,
                status_line: "No scan run yet.".to_string(),
                assessments: Vec::new(),
                scanning: false,
                include_oem: false,
                force_oem_refresh: false,
                driverpack_force_refresh: false,
                driverpack_running: false,
                driverpack_status: "No driver-pack lookup run yet.".to_string(),
                driverpack_result: None,
            },
            Task::none(),
        )
    }

    fn update(&mut self, message: Message) -> Task<Message> {
        match message {
            Message::ToggleOem(checked) => {
                // Kept purely for interface parity; see module docs. Not
                // wired to anything because there is nothing to include yet.
                self.include_oem = checked;
                if !checked {
                    self.force_oem_refresh = false;
                }
            }
            Message::ToggleForceOemRefresh(checked) => {
                self.force_oem_refresh = checked;
            }
            Message::Scan => {
                if self.scanning {
                    return Task::none(); // a scan is already in flight (mirrors the Python guard)
                }
                if self.engine.is_none() {
                    self.status_line = format!(
                        "Scan failed: engine unavailable ({})",
                        self.engine_error.as_deref().unwrap_or("unknown error")
                    );
                    return Task::none();
                };

                self.status_line = if self.include_oem && self.force_oem_refresh {
                    "Scanning… (force-refreshing OEM catalogs — re-downloading regardless of cache, may take a moment)".to_string()
                } else if self.include_oem {
                    "Scanning… (including OEM catalogs — first use downloads a real catalog, may take a moment)".to_string()
                } else {
                    "Scanning…".to_string()
                };

                // Mirrors `ScanWorker.run()`'s try/finally sources-restore
                // pattern (`gui/workers.py`): OEM sources are added to
                // `engine.sources` only for the duration of this one
                // `scan()` call and truncated back off again afterward, so
                // toggling the checkbox off before the next scan genuinely
                // takes effect and OEM sources never silently accumulate
                // across repeated scans left checked.
                let engine = self.engine.as_mut().unwrap();
                let original_len = engine.sources.len();
                let mut oem_build_error: Option<String> = None;
                if self.include_oem {
                    // `None` cache_dir: this GUI builds its engine with
                    // `build_default_engine(None, ...)`, so its
                    // `LocalCacheSource` is already using
                    // `paths::default_cache_dir()` — the same fallback
                    // `build_oem_sources(None)` resolves to, matching
                    // Python's `_oem_cache_dir()` None-fallback behavior
                    // (this GUI has no cache-dir override field).
                    match build_oem_sources(None) {
                        Ok(oem_sources) => {
                            let mut refresh_error = None;
                            for source in &oem_sources {
                                // force=false (default): only downloads if
                                // this cache_dir has never been refreshed
                                // before. force=true (force-refresh
                                // checkbox): re-download regardless of
                                // what's already cached.
                                if let Err(err) = source.refresh(self.force_oem_refresh) {
                                    refresh_error = Some(err);
                                    break;
                                }
                            }
                            match refresh_error {
                                Some(err) => oem_build_error = Some(err),
                                None => engine.sources.extend(oem_sources),
                            }
                        }
                        Err(err) => oem_build_error = Some(err),
                    }
                }

                let scan_result = if oem_build_error.is_none() {
                    Some(engine.scan())
                } else {
                    None
                };
                engine.sources.truncate(original_len); // always restore, regardless of outcome

                match (oem_build_error, scan_result) {
                    (Some(err), _) => {
                        self.status_line = format!("Scan failed: could not prepare OEM catalogs: {err}");
                    }
                    (None, Some(Ok(assessments))) => {
                        self.assessments = assessments;
                        let total = self.assessments.len();
                        let actionable = self
                            .assessments
                            .iter()
                            .filter(|a| {
                                matches!(a.status, DeviceStatus::Missing | DeviceStatus::Problem)
                            })
                            .count();
                        self.status_line = format!(
                            "Scan complete — {total} device(s) assessed, {actionable} need attention."
                        );
                    }
                    (None, Some(Err(err))) => {
                        self.status_line = format!("Scan failed: {err}");
                    }
                    (None, None) => unreachable!(),
                }
            }
            Message::ToggleDriverpackForceRefresh(checked) => {
                self.driverpack_force_refresh = checked;
            }
            Message::LookupDriverpacks => {
                if self.driverpack_running {
                    return Task::none(); // a lookup is already in flight (mirrors the Scan guard)
                }
                self.driverpack_running = true;

                // `detect_system_model()` is the same call `cmd_driverpack()`
                // (the CLI's real entry point) makes — see
                // `waypoint-platform::model`. Unvalidated on real hardware in
                // this sandbox (no `/sys/class/dmi/id`), same caveat as the
                // CLI subcommand.
                let model = match waypoint_platform::detect_system_model() {
                    Ok(model) => model,
                    Err(err) => {
                        self.driverpack_running = false;
                        self.driverpack_result = None;
                        self.driverpack_status =
                            format!("Driver-pack lookup failed: could not detect system model: {err}");
                        return Task::none();
                    }
                };

                // Reuses the CLI's own DI-seam function rather than
                // re-implementing the Dell/Lenovo/HP wiring here — see the
                // module doc at the top of this file. `cache_dir: None`
                // matches this GUI's other OEM-adjacent calls above, which
                // also fall back to `paths::default_cache_dir()`.
                let mut out: Vec<u8> = Vec::new();
                let mut err_buf: Vec<u8> = Vec::new();
                let outcome = waypoint_cli::cmd_driverpack_with_model(
                    &model,
                    None,
                    self.driverpack_force_refresh,
                    true, // json: this GUI parses the machine-readable shape, not the human-readable one
                    &mut out,
                    &mut err_buf,
                );
                self.driverpack_running = false;

                let warnings = String::from_utf8_lossy(&err_buf).trim().to_string();
                match outcome {
                    Ok(code) => match serde_json::from_slice::<serde_json::Value>(&out) {
                        Ok(json) => {
                            self.driverpack_result = Some(json);
                            self.driverpack_status = if code == waypoint_cli::EXIT_CLEAN {
                                "Driver pack(s) found.".to_string()
                            } else {
                                "No driver pack found for this model.".to_string()
                            };
                            if !warnings.is_empty() {
                                self.driverpack_status
                                    .push_str(&format!(" Warnings: {warnings}"));
                            }
                        }
                        Err(err) => {
                            self.driverpack_result = None;
                            self.driverpack_status =
                                format!("Driver-pack lookup failed: could not parse result: {err}");
                        }
                    },
                    Err(err) => {
                        self.driverpack_result = None;
                        self.driverpack_status = format!("Driver-pack lookup failed: {err}");
                    }
                }
            }
        }
        Task::none()
    }

    fn view(&self) -> Element<'_, Message> {
        let oem_checkbox = checkbox(self.include_oem)
            .label("Include OEM catalogs (Dell — downloads a real catalog on first use)")
            .on_toggle(Message::ToggleOem);

        let force_refresh_checkbox = if self.include_oem {
            checkbox(self.force_oem_refresh)
                .label("Force refresh (ignore cached catalog)")
                .on_toggle(Message::ToggleForceOemRefresh)
        } else {
            checkbox(false).label("Force refresh (ignore cached catalog)")
        };

        let mut counts: std::collections::HashMap<DeviceStatus, usize> = std::collections::HashMap::new();
        for a in &self.assessments {
            *counts.entry(a.status).or_insert(0) += 1;
        }

        let header = row![
            text("Device").width(Length::Fixed(240.0)),
            text("Class").width(Length::Fixed(110.0)),
            text("Status").width(Length::Fixed(130.0)),
            text("Candidate").width(Length::Fixed(110.0)),
            text("Source").width(Length::Fixed(110.0)),
            text("Signature").width(Length::Fixed(110.0)),
        ]
        .spacing(8);

        let mut rows: Column<Message> = Column::new().spacing(6);
        for tier in TIER_ORDER {
            let count = counts.get(&tier).copied().unwrap_or(0);
            rows = rows.push(text(format!("{} ({count})", tier_label(tier))).size(16));

            for a in self.assessments.iter().filter(|a| a.status == tier) {
                let candidate = a.candidates.first();
                let name = if a.ambiguous {
                    format!("{} [ambiguous]", a.device.friendly_name)
                } else {
                    a.device.friendly_name.clone()
                };
                let row_widget = row![
                    text(name).width(Length::Fixed(240.0)),
                    text(a.device.class_name.clone()).width(Length::Fixed(110.0)),
                    text(a.status.as_str()).width(Length::Fixed(130.0)),
                    text(candidate.map(|c| c.version.clone()).unwrap_or_else(|| "-".to_string()))
                        .width(Length::Fixed(110.0)),
                    text(candidate.map(|c| c.source_id.clone()).unwrap_or_else(|| "-".to_string()))
                        .width(Length::Fixed(110.0)),
                    text(
                        candidate
                            .map(|c| c.signature_type.as_str().to_string())
                            .unwrap_or_else(|| "-".to_string())
                    )
                    .width(Length::Fixed(110.0)),
                ]
                .spacing(8)
                .align_y(Alignment::Center);
                rows = rows.push(row_widget);
            }
        }

        let table = container(scrollable(rows)).height(Length::Fixed(360.0));

        // --- Driver-pack lookup section (GUI equivalent of `waypoint
        // driverpack`, see module docs above). Separate from Scan/OEM above
        // — this looks up whole model-level driver-pack bundles, not
        // per-device candidates, matching the CLI subcommand's own scope. ---
        let driverpack_force_refresh_checkbox = checkbox(self.driverpack_force_refresh)
            .label("Force refresh (ignore cached catalogs)")
            .on_toggle(Message::ToggleDriverpackForceRefresh);

        let driverpack_button = if self.driverpack_running {
            button("Looking up…")
        } else {
            button("Look up driver packs").on_press(Message::LookupDriverpacks)
        };

        let mut driverpack_column: Column<Message> = Column::new().spacing(6);
        if let Some(json) = &self.driverpack_result {
            let model = &json["model"];
            let display_or_dash = |v: &serde_json::Value| -> String {
                v.as_str().map(str::to_string).unwrap_or_else(|| "-".to_string())
            };
            driverpack_column = driverpack_column.push(text(format!(
                "Model: product_name={} sku_number={} baseboard_product={}",
                display_or_dash(&model["product_name"]),
                display_or_dash(&model["sku_number"]),
                display_or_dash(&model["baseboard_product"]),
            )));

            let dell = json["dell"].as_array().cloned().unwrap_or_default();
            driverpack_column = driverpack_column.push(text(format!("Dell ({})", dell.len())).size(16));
            for p in &dell {
                driverpack_column = driverpack_column.push(text(format!(
                    "  {} [{}] {} -> {}",
                    display_or_dash(&p["model_name"]),
                    display_or_dash(&p["os_label"]),
                    display_or_dash(&p["version"]),
                    display_or_dash(&p["url"]),
                )));
            }

            let lenovo = json["lenovo"].as_array().cloned().unwrap_or_default();
            driverpack_column = driverpack_column.push(text(format!("Lenovo ({})", lenovo.len())).size(16));
            for p in &lenovo {
                driverpack_column = driverpack_column.push(text(format!(
                    "  {} [{}] {} -> {}",
                    display_or_dash(&p["model_name"]),
                    display_or_dash(&p["os_label"]),
                    display_or_dash(&p["version"]),
                    display_or_dash(&p["url"]),
                )));
            }

            driverpack_column = driverpack_column.push(text("HP").size(16));
            if json["hp_supported"].is_null() {
                driverpack_column = driverpack_column.push(text("  no platform-list match"));
            } else {
                let hp = &json["hp_supported"];
                let os_count = hp["supported_os_descriptions"]
                    .as_array()
                    .map(|a| a.len())
                    .unwrap_or(0);
                driverpack_column = driverpack_column.push(text(format!(
                    "  {} — {os_count} supported OS description(s)",
                    display_or_dash(&hp["product_name"]),
                )));
            }
        }
        let driverpack_results = container(scrollable(driverpack_column)).height(Length::Fixed(160.0));

        let content = column![
            text("Waypoint Driver Manager").size(24),
            text(format!("Backend: {}", self.backend_label)),
            text(self.status_line.clone()),
            row![oem_checkbox, force_refresh_checkbox].spacing(20),
            header,
            table,
            button("Scan").on_press(Message::Scan),
            text("Driver-pack lookup (model-level, not per-device)").size(18),
            text(self.driverpack_status.clone()),
            row![driverpack_button, driverpack_force_refresh_checkbox].spacing(20),
            driverpack_results,
        ]
        .spacing(14)
        .padding(20);

        content.into()
    }
}

fn main() -> iced::Result {
    iced::application(WaypointApp::new, WaypointApp::update, WaypointApp::view)
        .title("Waypoint Driver Manager")
        .run()
}
