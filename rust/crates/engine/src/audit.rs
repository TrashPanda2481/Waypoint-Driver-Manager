//! Append-only, structured audit log. Direct port of `engine/audit.py`.
//!
//! Every scan, plan, install, and rollback goes through here as one
//! JSON-Lines record. This is the piece SDI has no equivalent of, and it's
//! required for any real IT-toolchain integration: a technician (or an RMM
//! script) needs to be able to answer "what did Waypoint actually do on
//! this machine, and when" without relying on memory or screenshots.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use serde_json::{Map, Value};
use time::OffsetDateTime;

pub struct AuditLog {
    log_path: PathBuf,
}

impl AuditLog {
    pub fn new(log_path: impl AsRef<Path>) -> Result<Self, String> {
        let log_path = log_path.as_ref().to_path_buf();
        if let Some(parent) = log_path.parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        Ok(Self { log_path })
    }

    /// Append one structured event. `fields` are merged in alongside the
    /// timestamp and event name, mirroring the Python `**fields` kwargs
    /// pattern via a `serde_json::Map`.
    pub fn record(&self, event: &str, fields: Map<String, Value>) -> Result<(), String> {
        let mut entry = Map::new();
        entry.insert(
            "timestamp".to_string(),
            Value::String(
                OffsetDateTime::now_utc()
                    .format(&time::format_description::well_known::Rfc3339)
                    .map_err(|e| e.to_string())?,
            ),
        );
        entry.insert("event".to_string(), Value::String(event.to_string()));
        for (k, v) in fields {
            entry.insert(k, v);
        }

        let line = serde_json::to_string(&Value::Object(entry)).map_err(|e| e.to_string())?;
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.log_path)
            .map_err(|e| e.to_string())?;
        writeln!(file, "{line}").map_err(|e| e.to_string())
    }

    pub fn read_all(&self) -> Result<Vec<Value>, String> {
        if !self.log_path.exists() {
            return Ok(Vec::new());
        }
        let raw = fs::read_to_string(&self.log_path).map_err(|e| e.to_string())?;
        raw.lines()
            .filter(|line| !line.trim().is_empty())
            .map(|line| serde_json::from_str(line).map_err(|e| e.to_string()))
            .collect()
    }
}

/// Small ergonomic helper for building the `fields` map at call sites,
/// e.g. `fields! { "device_count" => devices.len() }`.
#[macro_export]
macro_rules! audit_fields {
    ( $( $key:expr => $val:expr ),* $(,)? ) => {{
        let mut map = ::serde_json::Map::new();
        $( map.insert($key.to_string(), ::serde_json::json!($val)); )*
        map
    }};
}
