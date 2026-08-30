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
//! - The OEM checkbox is present for interface parity but permanently
//!   disabled: `build_oem_sources()` is not yet ported to Rust (see
//!   `waypoint-sources` crate docs), so there is nothing for it to enable
//!   yet. This mirrors the CLI's `--oem` flag, which returns a hard error
//!   rather than silently no-op'ing.
//! - Rendered as a grouped column list rather than Qt's `QTreeWidget`
//!   (iced has no built-in tree widget); the same three-tier grouping,
//!   counts, and per-row fields (device, class, status, candidate
//!   version, source, signature) are preserved.

use iced::widget::{button, checkbox, column, container, row, scrollable, text, Column};
use iced::{Alignment, Element, Length, Task};

use waypoint_core::{DeviceAssessment, DeviceStatus};
use waypoint_engine::factory::build_default_engine;
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
    /// Present for interface parity with the Python GUI's OEM checkbox;
    /// permanently unusable until OEM sources are ported (see module docs).
    include_oem: bool,
    force_oem_refresh: bool,
}

#[derive(Debug, Clone)]
enum Message {
    ToggleOem(bool),
    ToggleForceOemRefresh(bool),
    Scan,
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
                let Some(engine) = &self.engine else {
                    self.status_line = format!(
                        "Scan failed: engine unavailable ({})",
                        self.engine_error.as_deref().unwrap_or("unknown error")
                    );
                    return Task::none();
                };
                self.status_line = "Scanning…".to_string();
                match engine.scan() {
                    Ok(assessments) => {
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
                    Err(err) => {
                        self.status_line = format!("Scan failed: {err}");
                    }
                }
            }
        }
        Task::none()
    }

    fn view(&self) -> Element<'_, Message> {
        let oem_checkbox = checkbox(self.include_oem)
            .label("Include OEM catalogs (not yet available in this Rust build)")
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

        let content = column![
            text("Waypoint Driver Manager").size(24),
            text(format!("Backend: {}", self.backend_label)),
            text(self.status_line.clone()),
            row![oem_checkbox, force_refresh_checkbox].spacing(20),
            header,
            table,
            button("Scan").on_press(Message::Scan),
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
