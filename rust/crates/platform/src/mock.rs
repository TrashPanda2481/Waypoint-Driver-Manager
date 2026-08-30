//! In-memory mock backend used by tests and by GUI/CLI development without
//! real hardware. Direct port of `platform/mock.py`. This is what keeps
//! core/engine logic testable on Linux CI even though the primary target
//! platform is Windows.

use std::cell::RefCell;

use waypoint_core::Device;

use crate::base::{DeviceBackend, InstallResult};

pub struct MockDeviceBackend {
    devices: Vec<Device>,
    pub backup_calls: RefCell<Vec<(String, String)>>,
    pub install_calls: RefCell<Vec<(String, String, bool)>>,
    pub restore_point_calls: RefCell<Vec<String>>,
    pub restore_point_should_succeed: bool,
}

impl MockDeviceBackend {
    pub fn new(devices: Vec<Device>) -> Self {
        Self {
            devices,
            backup_calls: RefCell::new(Vec::new()),
            install_calls: RefCell::new(Vec::new()),
            restore_point_calls: RefCell::new(Vec::new()),
            restore_point_should_succeed: true,
        }
    }
}

impl DeviceBackend for MockDeviceBackend {
    fn name(&self) -> &'static str {
        "MockDeviceBackend"
    }

    fn enumerate_devices(&self) -> Result<Vec<Device>, String> {
        Ok(self.devices.clone())
    }

    fn export_driver_backup(&self, device: &Device, dest_dir: &str) -> Result<String, String> {
        self.backup_calls
            .borrow_mut()
            .push((device.instance_id.clone(), dest_dir.to_string()));
        Ok(format!("{dest_dir}/{}.backup", device.instance_id))
    }

    fn install_driver(&self, device: &Device, package_path: &str, dry_run: bool) -> InstallResult {
        self.install_calls.borrow_mut().push((
            device.instance_id.clone(),
            package_path.to_string(),
            dry_run,
        ));
        let command = format!("pnputil /add-driver {package_path} /install");
        if dry_run {
            InstallResult {
                success: true,
                commands_run: vec![command],
                message: "dry-run: no changes made".to_string(),
            }
        } else {
            InstallResult {
                success: true,
                commands_run: vec![command],
                message: "installed".to_string(),
            }
        }
    }

    fn create_restore_point(&self, description: &str) -> bool {
        self.restore_point_calls
            .borrow_mut()
            .push(description.to_string());
        self.restore_point_should_succeed
    }
}
