//! System model detection for OEM driver-pack lookups. New in the Rust
//! port — no Python equivalent exists (`build_oem_sources()` in
//! `engine/factory.py` explicitly says model-based lookups are a
//! separate, not-yet-built concern; see that docstring). This is
//! greenfield, not a straight port, so every field here is documented
//! against a specific, checked real-world source rather than assumed.
//!
//! Deliberately NOT part of `DeviceBackend` — that trait's job is
//! per-device enumeration (`enumerate_devices`), a fundamentally
//! different concern from reading firmware-level system identity once
//! per run. Kept as its own free function, mirroring
//! `build_default_backend()`'s shape.
//!
//! **Dell's own documented caveat, not an assumption:** Dell's driver
//! pack catalog KB
//! (<https://www.dell.com/support/kbdoc/en-us/000122176/driver-pack-catalog>)
//! states the catalog's `systemID` attribute "is not readily accessible
//! [via a] WMI query" and recommends matching by the catalog's `name`
//! attribute (the human model string, e.g. "OptiPlex 5070") instead when
//! running on Windows. On Linux, by contrast, the same systemID *is*
//! directly readable — it's SMBIOS's "SKU Number" field, confirmed
//! against a real `dmidecode -t system` capture showing `SKU Number:
//! 0A7E` in the same 4-character hex style as the driver-pack catalog's
//! `systemID` values (<https://sleeplessbeastie.eu/2025/04/03/how-to-determine-computer-manufacturer-and-model/>).
//! So `sku_number` and `product_name` are both collected, and callers
//! (see `DellDriverPackSource::packs_for_model`) should try `sku_number`
//! first and fall back to `product_name` if it's absent or unmatched.

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SystemModel {
    /// The human-readable model/product name — Dell's catalog `name`
    /// attribute (e.g. "OptiPlex 5070"), Lenovo's full SMBIOS
    /// system-product-name (first 4 characters are the machine-type code
    /// `LenovoDriverPackSource::packs_for_model` matches on).
    /// Linux: `/sys/class/dmi/id/product_name`. Windows:
    /// `Win32_ComputerSystem.Model`.
    pub product_name: Option<String>,
    /// Dell's driver-pack catalog `systemID` (SMBIOS SKU Number).
    /// Linux: `/sys/class/dmi/id/product_sku` (equivalent to `dmidecode
    /// -s system-sku-number`). Windows: `Win32_ComputerSystem.
    /// SystemSKUNumber` — reported by community sysadmin references
    /// (e.g. <https://garytown.com/configmgr-inventory-systemskunumber>)
    /// as the WMI-exposed SKU field, distinct from `Win32_ComputerSystemProduct.
    /// IdentifyingNumber` (which is a serial/service-tag field, not the
    /// SKU — confirmed against Dell's own KB above).
    pub sku_number: Option<String>,
    /// HP's `SystemID`, per the existing `hp_platform.rs` module comment:
    /// read from `Win32_BaseBoard.Product` on real HP hardware. Linux:
    /// `/sys/class/dmi/id/board_name` (SMBIOS baseboard product name —
    /// the same DMI Type 2 field `Win32_BaseBoard.Product` reads on
    /// Windows).
    pub baseboard_product: Option<String>,
}

impl SystemModel {
    pub fn is_empty(&self) -> bool {
        self.product_name.is_none() && self.sku_number.is_none() && self.baseboard_product.is_none()
    }
}

/// Detect the current system's model identity. Never hard-errors on a
/// missing/unreadable individual field — a `SystemModel` with some or
/// all fields `None` is a valid, honestly-reported result (e.g. a VM
/// with no real SMBIOS data), not a failure. Only returns `Err` if this
/// platform has no detection path implemented at all.
#[cfg(target_os = "linux")]
pub fn detect_system_model() -> Result<SystemModel, String> {
    Ok(SystemModel {
        product_name: read_dmi_sysfs("product_name"),
        sku_number: read_dmi_sysfs("product_sku"),
        baseboard_product: read_dmi_sysfs("board_name"),
    })
}

#[cfg(target_os = "linux")]
fn read_dmi_sysfs(field: &str) -> Option<String> {
    // Not run against a real DMI table in this sandbox (a container with
    // no /sys/class/dmi/id at all) — validate on real hardware/VM before
    // trusting. Some of these files require root on some distros; a
    // permission error here is treated the same as "field absent"
    // (`None`), not a hard error for the whole detection.
    let raw = std::fs::read_to_string(format!("/sys/class/dmi/id/{field}")).ok()?;
    let trimmed = raw.trim();
    if trimmed.is_empty() || trimmed.eq_ignore_ascii_case("none") || trimmed.eq_ignore_ascii_case("not specified") {
        None
    } else {
        Some(trimmed.to_string())
    }
}

#[cfg(target_os = "windows")]
pub fn detect_system_model() -> Result<SystemModel, String> {
    use serde::Deserialize;
    use wmi::{COMLibrary, WMIConnection};

    #[derive(Deserialize, Debug)]
    #[serde(rename = "Win32_ComputerSystem")]
    #[allow(non_snake_case)]
    struct Win32ComputerSystem {
        Model: Option<String>,
        SystemSKUNumber: Option<String>,
    }

    #[derive(Deserialize, Debug)]
    #[serde(rename = "Win32_BaseBoard")]
    #[allow(non_snake_case)]
    struct Win32BaseBoard {
        Product: Option<String>,
    }

    // Not compiled or run on a real Windows machine in this sandbox (no
    // Windows toolchain available here) — same caveat as
    // `WindowsDeviceBackend`. Treat as a first pass to validate on real
    // hardware before trusting.
    let com = COMLibrary::new().map_err(|e| e.to_string())?;
    let conn = WMIConnection::new(com).map_err(|e| e.to_string())?;

    let cs: Vec<Win32ComputerSystem> = conn.query().map_err(|e| e.to_string())?;
    let bb: Vec<Win32BaseBoard> = conn.query().map_err(|e| e.to_string())?;

    let (product_name, sku_number) = cs
        .into_iter()
        .next()
        .map(|c| (c.Model, c.SystemSKUNumber))
        .unwrap_or((None, None));
    let baseboard_product = bb.into_iter().next().and_then(|b| b.Product);

    Ok(SystemModel {
        product_name: non_empty(product_name),
        sku_number: non_empty(sku_number),
        baseboard_product: non_empty(baseboard_product),
    })
}

#[cfg(target_os = "windows")]
fn non_empty(value: Option<String>) -> Option<String> {
    value.filter(|s| !s.trim().is_empty())
}

#[cfg(not(any(target_os = "linux", target_os = "windows")))]
pub fn detect_system_model() -> Result<SystemModel, String> {
    Err("system model detection is not implemented on this platform".to_string())
}
