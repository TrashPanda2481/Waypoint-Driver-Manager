//! Windows device backend. Direct port of `platform/windows.py`.
//!
//! Built against documented, real Windows tooling — not a placeholder API.
//! This module only compiles on `cfg(target_os = "windows")` (the `wmi`
//! crate dependency itself is Windows-only in `Cargo.toml`), matching the
//! Python version's lazy `import wmi` inside methods so the crate still
//! builds cleanly on Linux for testing/CI.
//!
//! It has NOT been run against real hardware or even compiled on a Windows
//! target yet (this port was written and reviewed in a Linux sandbox with
//! no Windows toolchain available) — treat as a first implementation pass
//! to validate on the Dell/Asus dev machines the same way Meridian OS
//! components are validated before being called "working."
//!
//! References:
//! - Device enumeration: WMI `Win32_PnPEntity` / `Win32_PnPSignedDriver`.
//! - Install: `pnputil /add-driver <inf> /install`
//!   <https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples>
//! - Backup: `pnputil /export-driver <published name> <dest>`
//! - Restore point: `SystemRestore.CreateRestorePoint` via WMI
//!   (`SystemRestore` class on `root\default` namespace), verified by
//!   checking the return code.

use std::process::Command;

use serde::{Deserialize, Serialize};
use time::Date;
use wmi::{COMLibrary, FilterValue, WMIConnection};

use waypoint_core::{Device, InstalledDriver, SignatureType};

use crate::base::{DeviceBackend, InstallResult};

#[derive(Deserialize, Debug)]
#[serde(rename = "Win32_PnPEntity")]
#[allow(non_snake_case)]
struct Win32PnpEntity {
    DeviceID: String,
    HardwareID: Option<Vec<String>>,
    ClassGuid: Option<String>,
    PNPClass: Option<String>,
    Name: Option<String>,
    ConfigManagerErrorCode: Option<i32>,
}

/// Marker type for `WMIConnection::exec_class_method`'s `Class` generic —
/// identifies which WMI class (on the `root\default` namespace connection)
/// the method belongs to. Never queried directly, only used as a type tag.
#[derive(Deserialize)]
#[allow(non_snake_case, dead_code)]
struct SystemRestore;

#[derive(Serialize)]
#[allow(non_snake_case)]
struct CreateRestorePointInput {
    Description: String,
    EventType: i32,
    RestorePointType: i32,
}

#[derive(Deserialize)]
#[allow(non_snake_case)]
struct CreateRestorePointOutput {
    ReturnValue: i32,
}

#[derive(Deserialize, Debug)]
#[serde(rename = "Win32_PnPSignedDriver")]
#[allow(non_snake_case)]
struct Win32PnpSignedDriver {
    DeviceID: Option<String>,
    DriverVersion: Option<String>,
    DriverDate: Option<String>,
    DriverProviderName: Option<String>,
    IsSigned: Option<bool>,
    InfName: Option<String>,
}

/// `Win32_PnPSignedDriver.DriverSignForm`/`IsSigned` does not map 1:1 to
/// WHQL vs attestation; that distinction needs `Get-WindowsDriver` or
/// catalog inspection. This mapping is a first pass — flagged as an open
/// item in docs/Architecture.md rather than silently guessed.
fn map_signature(is_signed: bool) -> SignatureType {
    if is_signed {
        SignatureType::Whql
    } else {
        SignatureType::Unsigned
    }
}

/// Parse the WMI `DriverDate` string, format `yyyymmddHHMMSS.mmmmmm+UUU`.
fn parse_driver_date(raw: &str) -> Option<Date> {
    if raw.len() < 8 {
        return None;
    }
    let year: i32 = raw[0..4].parse().ok()?;
    let month: u8 = raw[4..6].parse().ok()?;
    let day: u8 = raw[6..8].parse().ok()?;
    let month = time::Month::try_from(month).ok()?;
    Date::from_calendar_date(year, month, day).ok()
}

pub struct WindowsDeviceBackend {
    com: COMLibrary,
}

impl WindowsDeviceBackend {
    pub fn new() -> Result<Self, String> {
        let com = COMLibrary::new().map_err(|e| e.to_string())?;
        Ok(Self { com })
    }

    fn connect(&self) -> Result<WMIConnection, String> {
        WMIConnection::new(self.com).map_err(|e| e.to_string())
    }

    fn lookup_installed_driver(
        conn: &WMIConnection,
        device_id: &str,
    ) -> Option<InstalledDriver> {
        let filter = std::collections::HashMap::from([(
            "DeviceID".to_string(),
            FilterValue::Str(device_id),
        )]);
        let results: Vec<Win32PnpSignedDriver> =
            conn.filtered_query(&filter).ok()?;
        let signed = results.into_iter().next()?;
        let driver_date = signed.DriverDate.as_deref().and_then(parse_driver_date);
        Some(InstalledDriver {
            version: signed.DriverVersion.unwrap_or_else(|| "unknown".to_string()),
            driver_date,
            publisher: signed
                .DriverProviderName
                .unwrap_or_else(|| "unknown".to_string()),
            signature_type: map_signature(signed.IsSigned.unwrap_or(false)),
            inf_path: signed.InfName,
        })
    }
}

impl DeviceBackend for WindowsDeviceBackend {
    fn name(&self) -> &'static str {
        "WindowsDeviceBackend"
    }

    fn enumerate_devices(&self) -> Result<Vec<Device>, String> {
        let conn = self.connect()?;
        let entries: Vec<Win32PnpEntity> = conn.query().map_err(|e| e.to_string())?;

        let mut devices = Vec::new();
        for entry in entries {
            let hwids = entry.HardwareID.unwrap_or_default();
            if hwids.is_empty() {
                continue;
            }
            let installed = Self::lookup_installed_driver(&conn, &entry.DeviceID);
            devices.push(Device {
                hwids,
                class_guid: entry.ClassGuid.unwrap_or_default(),
                class_name: entry.PNPClass.unwrap_or_else(|| "Unknown".to_string()),
                friendly_name: entry.Name.unwrap_or_else(|| entry.DeviceID.clone()),
                instance_id: entry.DeviceID,
                problem_code: entry.ConfigManagerErrorCode,
                installed,
            });
        }
        Ok(devices)
    }

    fn export_driver_backup(&self, device: &Device, dest_dir: &str) -> Result<String, String> {
        let inf_path = device
            .installed
            .as_ref()
            .and_then(|i| i.inf_path.as_ref())
            .ok_or_else(|| {
                format!(
                    "No installed driver INF known for {}, cannot back up.",
                    device.instance_id
                )
            })?;
        let dest = format!("{dest_dir}\\{}", device.instance_id.replace('\\', "_"));
        let output = Command::new("pnputil")
            .args(["/export-driver", inf_path, &dest])
            .output()
            .map_err(|e| e.to_string())?;
        if !output.status.success() {
            return Err(String::from_utf8_lossy(&output.stderr).to_string());
        }
        Ok(dest)
    }

    fn install_driver(&self, _device: &Device, package_path: &str, dry_run: bool) -> InstallResult {
        let command = format!("pnputil /add-driver \"{package_path}\" /install");
        if dry_run {
            return InstallResult {
                success: true,
                commands_run: vec![command],
                message: "dry-run: no changes made".to_string(),
            };
        }
        match Command::new("pnputil")
            .args(["/add-driver", package_path, "/install"])
            .output()
        {
            Ok(output) => {
                let message = if output.stdout.is_empty() {
                    String::from_utf8_lossy(&output.stderr).to_string()
                } else {
                    String::from_utf8_lossy(&output.stdout).to_string()
                };
                InstallResult {
                    success: output.status.success(),
                    commands_run: vec![command],
                    message,
                }
            }
            Err(e) => InstallResult {
                success: false,
                commands_run: vec![command],
                message: e.to_string(),
            },
        }
    }

    fn create_restore_point(&self, description: &str) -> bool {
        // WMI SystemRestore.CreateRestorePoint(Description, EventType, RestorePointType)
        // EventType 100 = BEGIN_SYSTEM_CHANGE, RestorePointType 0 = APPLICATION_INSTALL.
        // Return value 0 indicates success — anything else must block the
        // install batch per docs/Architecture.md section 3.3. Any WMI/COM
        // error here must fail closed (block the batch), not propagate.
        let Ok(com) = COMLibrary::new() else { return false };
        let Ok(conn) = WMIConnection::with_namespace_path("root\\default", com) else {
            return false;
        };
        let input = CreateRestorePointInput {
            Description: description.to_string(),
            EventType: 100,
            RestorePointType: 0,
        };
        match conn.exec_class_method::<SystemRestore, CreateRestorePointOutput>(
            "CreateRestorePoint",
            input,
        ) {
            Ok(output) => output.ReturnValue == 0,
            Err(_) => false,
        }
    }
}
