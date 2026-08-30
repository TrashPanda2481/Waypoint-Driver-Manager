//! Linux device backend. Direct port of `platform/linux.py`.
//!
//! Scoped narrowly per docs/Architecture.md section 6 ("Open Decisions"):
//! this exists mainly to keep `waypoint-core`/`waypoint-engine` genuinely
//! cross-platform and testable, not as a full parity implementation of
//! Windows-style driver management. Linux driver "installation" is a
//! fundamentally different problem (kernel modules + firmware blobs are
//! managed by the distro/kernel, not INF packages) — this backend reports
//! what's bound today via udev/lspci and is a stretch goal, not the
//! primary target.

use std::process::Command;

use waypoint_core::{Device, InstalledDriver, SignatureType};

use crate::base::{DeviceBackend, InstallResult};

pub struct LinuxDeviceBackend;

impl LinuxDeviceBackend {
    pub fn new() -> Self {
        Self
    }
}

impl Default for LinuxDeviceBackend {
    fn default() -> Self {
        Self::new()
    }
}

impl DeviceBackend for LinuxDeviceBackend {
    fn name(&self) -> &'static str {
        "LinuxDeviceBackend"
    }

    fn enumerate_devices(&self) -> Result<Vec<Device>, String> {
        let mut enumerator = udev::Enumerator::new().map_err(|e| e.to_string())?;
        enumerator
            .match_subsystem("pci")
            .map_err(|e| e.to_string())?;
        let scanned = enumerator.scan_devices().map_err(|e| e.to_string())?;

        let mut devices = Vec::new();
        for dev in scanned {
            let hwid = dev
                .property_value("PCI_ID")
                .and_then(|v| v.to_str())
                .map(|s| s.to_string());
            let driver = dev.driver().and_then(|d| d.to_str());
            let class_guid = dev
                .property_value("PCI_CLASS")
                .and_then(|v| v.to_str())
                .unwrap_or("")
                .to_string();
            let friendly_name = dev
                .sysname()
                .to_str()
                .unwrap_or_default()
                .to_string();
            let instance_id = dev.syspath().to_string_lossy().to_string();

            devices.push(Device {
                hwids: hwid.into_iter().collect(),
                class_guid,
                class_name: "PCI".to_string(),
                friendly_name,
                instance_id,
                problem_code: if driver.is_some() { None } else { Some(28) },
                installed: driver.map(|_| InstalledDriver {
                    version: "kernel-bundled".to_string(),
                    driver_date: None,
                    publisher: "Linux kernel".to_string(),
                    signature_type: SignatureType::Whql,
                    inf_path: None,
                }),
            });
        }
        Ok(devices)
    }

    fn export_driver_backup(&self, _device: &Device, _dest_dir: &str) -> Result<String, String> {
        // Kernel modules aren't "exported" the way Windows driver packages
        // are; this method exists for trait/interface parity only.
        Err("Driver backup is not applicable to Linux kernel modules.".to_string())
    }

    fn install_driver(&self, _device: &Device, package_path: &str, dry_run: bool) -> InstallResult {
        let command = format!("modprobe {package_path}");
        if dry_run {
            return InstallResult {
                success: true,
                commands_run: vec![command],
                message: "dry-run: no changes made".to_string(),
            };
        }
        match Command::new("modprobe").arg(package_path).output() {
            Ok(output) => InstallResult {
                success: output.status.success(),
                commands_run: vec![command],
                message: String::from_utf8_lossy(&output.stderr).to_string(),
            },
            Err(e) => InstallResult {
                success: false,
                commands_run: vec![command],
                message: e.to_string(),
            },
        }
    }

    fn create_restore_point(&self, _description: &str) -> bool {
        // No universal equivalent; a Btrfs/LVM snapshot hook is a
        // reasonable future extension point but out of scope for v1.
        false
    }
}
