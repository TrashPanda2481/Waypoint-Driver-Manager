//! Engine: the single orchestration path both the GUI and CLI call
//! through. Direct port of `engine/session.py`.
//!
//! scan -> plan -> (confirm) -> backup -> install -> verify, with rollback
//! as a first-class operation. Keeping exactly one code path here is what
//! prevents the GUI and CLI from silently behaving differently — a real
//! gap in SDI, where interactive and `-autoinstall` flows aren't
//! guaranteed to agree. See docs/Architecture.md section 3.4.

use std::collections::{HashMap, HashSet};

use serde::{Deserialize, Serialize};

use waypoint_core::{assess_all, Device, DeviceAssessment, DeviceStatus, DriverCandidate, SignatureType};
use waypoint_platform::{DeviceBackend, InstallResult};
use waypoint_sources::DriverSource;

use crate::audit::AuditLog;
use crate::audit_fields;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlanEntry {
    pub instance_id: String,
    pub friendly_name: String,
    pub status: String,
    pub chosen_candidate: Option<DriverCandidate>,
    pub ambiguous: bool,
    pub requires_confirmation: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Plan {
    pub entries: Vec<PlanEntry>,
}

#[derive(Debug, Clone, Serialize)]
pub struct ApplyResult {
    pub instance_id: String,
    pub success: bool,
    pub commands: Vec<String>,
    pub message: String,
}

pub struct WaypointEngine {
    pub backend: Box<dyn DeviceBackend>,
    pub sources: Vec<Box<dyn DriverSource>>,
    pub audit: AuditLog,
    pub min_signature: SignatureType,
}

impl WaypointEngine {
    pub fn new(
        backend: Box<dyn DeviceBackend>,
        sources: Vec<Box<dyn DriverSource>>,
        audit: AuditLog,
        min_signature: SignatureType,
    ) -> Self {
        Self {
            backend,
            sources,
            audit,
            min_signature,
        }
    }

    pub fn scan(&self) -> Result<Vec<DeviceAssessment>, String> {
        let devices = self.backend.enumerate_devices()?;
        self.audit
            .record("scan", audit_fields! { "device_count" => devices.len() })?;

        let mut all_hwids: Vec<String> = devices
            .iter()
            .flat_map(|d| d.hwids.iter().cloned())
            .collect::<HashSet<_>>()
            .into_iter()
            .collect();
        all_hwids.sort();

        let mut candidates_by_hwid: HashMap<String, Vec<DriverCandidate>> =
            all_hwids.iter().map(|h| (h.clone(), Vec::new())).collect();
        for source in &self.sources {
            let found = source.search(&all_hwids)?;
            self.audit.record(
                "source_search",
                audit_fields! {
                    "source_id" => source.source_id(),
                    "candidates_found" => found.len(),
                },
            )?;
            for candidate in found {
                candidates_by_hwid
                    .entry(candidate.hwid.clone())
                    .or_default()
                    .push(candidate);
            }
        }

        let assessments = assess_all(&devices, &candidates_by_hwid, self.min_signature);
        let missing = assessments.iter().filter(|a| a.status == DeviceStatus::Missing).count();
        let problem = assessments.iter().filter(|a| a.status == DeviceStatus::Problem).count();
        let upgrade_available = assessments
            .iter()
            .filter(|a| a.status == DeviceStatus::UpgradeAvailable)
            .count();
        self.audit.record(
            "assessment_complete",
            audit_fields! {
                "missing" => missing,
                "problem" => problem,
                "upgrade_available" => upgrade_available,
            },
        )?;
        Ok(assessments)
    }

    /// Turn assessments into a concrete plan. Ambiguous devices and
    /// anything in the `upgrade_available` tier are marked
    /// `requires_confirmation = true` by default — never auto-selected.
    /// See docs/Architecture.md section 3.1.
    pub fn build_plan(&self, assessments: &[DeviceAssessment]) -> Result<Plan, String> {
        let entries: Vec<PlanEntry> = assessments
            .iter()
            .map(|assessment| {
                let chosen = assessment.candidates.first().cloned();
                let requires_confirmation =
                    assessment.ambiguous || assessment.status == DeviceStatus::UpgradeAvailable;
                PlanEntry {
                    instance_id: assessment.device.instance_id.clone(),
                    friendly_name: assessment.device.friendly_name.clone(),
                    status: assessment.status.as_str().to_string(),
                    chosen_candidate: chosen,
                    ambiguous: assessment.ambiguous,
                    requires_confirmation,
                }
            })
            .collect();
        let plan = Plan { entries };
        self.audit
            .record("plan_built", audit_fields! { "entry_count" => plan.entries.len() })?;
        Ok(plan)
    }

    /// Apply a plan. Anything with `requires_confirmation = true` is
    /// skipped unless its `instance_id` is present in
    /// `confirmed_instance_ids` — the engine enforces this, it is not left
    /// to callers to remember.
    pub fn apply(
        &self,
        plan: &Plan,
        devices_by_instance_id: &HashMap<String, Device>,
        dry_run: bool,
        backup_dir: &str,
        confirmed_instance_ids: &HashSet<String>,
    ) -> Result<Vec<ApplyResult>, String> {
        let mut results = Vec::new();

        if !dry_run {
            let restore_ok = self
                .backend
                .create_restore_point("Waypoint Driver Manager batch install");
            self.audit
                .record("restore_point", audit_fields! { "success" => restore_ok })?;
            if !restore_ok {
                self.audit
                    .record("apply_aborted", audit_fields! { "reason" => "restore_point_failed" })?;
                return Err(
                    "Restore point creation failed or could not be verified — aborting install \
                     batch. This is the exact failure mode SDI ticket #108 hit; Waypoint blocks \
                     on it instead of proceeding."
                        .to_string(),
                );
            }
        }

        for entry in &plan.entries {
            let Some(chosen) = &entry.chosen_candidate else {
                continue;
            };
            if entry.requires_confirmation && !confirmed_instance_ids.contains(&entry.instance_id) {
                self.audit.record(
                    "skip_unconfirmed",
                    audit_fields! { "instance_id" => entry.instance_id.clone() },
                )?;
                continue;
            }

            let device = devices_by_instance_id
                .get(&entry.instance_id)
                .ok_or_else(|| format!("Device {} not found in devices_by_instance_id", entry.instance_id))?;

            if !dry_run && device.installed.is_some() {
                let backup_path = self.backend.export_driver_backup(device, backup_dir)?;
                self.audit.record(
                    "backup",
                    audit_fields! {
                        "instance_id" => entry.instance_id.clone(),
                        "backup_path" => backup_path,
                    },
                )?;
            }

            let package_path = &chosen.download_uri;
            let result: InstallResult = self.backend.install_driver(device, package_path, dry_run);
            self.audit.record(
                "install",
                audit_fields! {
                    "instance_id" => entry.instance_id.clone(),
                    "dry_run" => dry_run,
                    "success" => result.success,
                    "commands" => result.commands_run.clone(),
                },
            )?;
            results.push(ApplyResult {
                instance_id: entry.instance_id.clone(),
                success: result.success,
                commands: result.commands_run,
                message: result.message,
            });
        }
        Ok(results)
    }
}
