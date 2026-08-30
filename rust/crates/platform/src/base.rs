//! Platform backend interface. Direct port of `platform/base.py`.
//!
//! Every OS-specific device enumeration/install backend implements this
//! trait. `waypoint-engine` only ever depends on this trait, never on
//! `windows`/`wmi`/`udev` directly — that's what lets the engine be
//! unit-tested on any OS via `MockDeviceBackend`.

use waypoint_core::Device;

#[derive(Debug, Clone, Default)]
pub struct InstallResult {
    pub success: bool,
    pub commands_run: Vec<String>,
    pub message: String,
}

pub trait DeviceBackend {
    /// Return every present device with its current driver binding.
    fn enumerate_devices(&self) -> Result<Vec<Device>, String>;

    /// Back up the currently-bound driver package for `device` to
    /// `dest_dir`. Must succeed and return the backup path *before* the
    /// engine is allowed to proceed with an install for that device.
    fn export_driver_backup(&self, device: &Device, dest_dir: &str) -> Result<String, String>;

    /// Install the driver package at `package_path` for `device`.
    ///
    /// When `dry_run` is true, backends must not mutate system state —
    /// they should return the exact command(s) they *would* run so the
    /// caller can display/log it.
    fn install_driver(&self, device: &Device, package_path: &str, dry_run: bool) -> InstallResult;

    /// Create an OS-level restore point (Windows System Restore, or a
    /// Linux-side equivalent snapshot hook). Returns true only on verified
    /// success — false here must block the install batch. This directly
    /// addresses SDI's known failure mode where a restore point silently
    /// didn't work. See docs/Architecture.md section 3.3.
    fn create_restore_point(&self, description: &str) -> bool;

    /// Backend identifier for display purposes (the GUI's "Backend: ..."
    /// label uses this — Python's version reads `type(backend).__name__`
    /// off the concrete class; trait objects need this spelled out
    /// explicitly since there's no reflection).
    fn name(&self) -> &'static str;
}
