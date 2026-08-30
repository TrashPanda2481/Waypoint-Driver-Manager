//! `waypoint-platform`: OS-specific device enumeration/install backends.
//!
//! Mirrors `src/waypoint/platform/` from the Python implementation. Core
//! matching logic and the engine only ever depend on the `DeviceBackend`
//! trait in [`base`], never on `windows`/`wmi`/`udev` directly — that's
//! what lets `waypoint-core`/`waypoint-engine` be unit-tested on any OS via
//! [`mock::MockDeviceBackend`].

pub mod base;
pub mod mock;

#[cfg(target_os = "linux")]
pub mod linux;

#[cfg(target_os = "windows")]
pub mod windows;

pub use base::{DeviceBackend, InstallResult};
pub use mock::MockDeviceBackend;

#[cfg(target_os = "linux")]
pub use linux::LinuxDeviceBackend;

#[cfg(target_os = "windows")]
pub use windows::WindowsDeviceBackend;

/// Pick the real backend for the current OS. Direct port of
/// `engine/factory.py::build_default_backend`. Constructing the backend
/// never fails here — only calling `enumerate_devices()` can, and callers
/// are responsible for surfacing that (mirrors the Python docstring).
#[cfg(target_os = "windows")]
pub fn build_default_backend() -> Result<Box<dyn DeviceBackend>, String> {
    Ok(Box::new(WindowsDeviceBackend::new()?))
}

#[cfg(target_os = "linux")]
pub fn build_default_backend() -> Result<Box<dyn DeviceBackend>, String> {
    Ok(Box::new(LinuxDeviceBackend::new()))
}

#[cfg(not(any(target_os = "windows", target_os = "linux")))]
pub fn build_default_backend() -> Result<Box<dyn DeviceBackend>, String> {
    Err("No device backend is available for this platform.".to_string())
}
