//! `waypoint-engine`: the single orchestration path (scan -> plan ->
//! confirm -> backup -> install -> verify) both the CLI and GUI call
//! through, plus the audit log and default backend/source wiring.
//! Mirrors `src/waypoint/engine/` and `src/waypoint/paths.py`.

pub mod audit;
pub mod factory;
pub mod paths;
pub mod session;

pub use audit::AuditLog;
pub use factory::{build_default_backend, build_default_engine, build_default_sources};
pub use session::{ApplyResult, Plan, PlanEntry, WaypointEngine};
