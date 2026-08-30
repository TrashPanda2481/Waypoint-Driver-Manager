# Rust Port — Scope and Status

This document tracks what has and has not been ported from the Python
implementation (`src/waypoint/`) to the Rust workspace (`rust/`), and why.
Written for anyone picking this branch (`rust-rewrite`) back up later —
including future-me — so nothing here has to be re-derived from scratch or
taken on faith.

**Ground rule carried over from the rest of this project:** nothing below
is described as done, tested, or working unless it actually compiled and
(where applicable) actually ran in this sandbox. Where something is real
code but unverified, that is stated explicitly rather than implied.

## Ported and verified (compiles + passes tests in this Linux sandbox)

| Python module | Rust crate/module | Verification |
|---|---|---|
| `core/models.py` | `waypoint-core::models` | 8/8 ported unit tests pass (`crates/core/tests/matching_tests.rs`, full port of `tests/test_matching.py`) |
| `core/matching.py` | `waypoint-core::matching` | same test file as above |
| `platform/base.py` | `waypoint-platform::base` (`DeviceBackend` trait) | compiles; exercised via mock + linux backends below |
| `platform/mock.py` | `waypoint-platform::mock` | used directly by the 3 engine integration tests |
| `platform/linux.py` | `waypoint-platform::linux` (udev-based) | compiles against real `libudev-dev`; ran for real against this sandbox's PCI bus via `waypoint-cli scan` (returned 0 devices — expected, sandbox has no PCI-visible display/network hardware exposed to udev) |
| `sources/base.py` | `waypoint-sources::base` (`DriverSource` trait) | compiles |
| `sources/local_cache.py` | `waypoint-sources::local_cache` | SHA-256 verified via `sha2` crate; exercised by engine integration tests (`add_package` + `search`) |
| `engine/audit.py` | `waypoint-engine::audit` | JSON-Lines output verified by hand (`cat /tmp/waypoint-audit.jsonl` after a real CLI run) |
| `engine/session.py` | `waypoint-engine::session` (`WaypointEngine`) | 3/3 ported integration tests pass (`crates/engine/tests/engine_tests.rs`, full port of `tests/test_engine.py`) — covers the two safety-critical behaviors: restore-point-failure abort, and unconfirmed upgrade-tier entries never auto-applying |
| `engine/factory.py` (`build_default_backend`, `build_default_sources`, `build_default_engine`) | `waypoint-engine::factory` | exercised for real by `waypoint-cli scan`/`plan` against the live Linux backend |
| `paths.py` | `waypoint-engine::paths` | small, direct port; not independently unit-tested (source Python module has no dedicated test file either) |
| `cli/main.py` (`scan`, `plan`) | `waypoint-cli` | ran for real: `scan --json`, `plan --json`, correct exit codes (0/1/2), audit log written |
| `cli/main.py` (`apply`) | `waypoint-cli` | **intentionally still a stub in both languages** — the Python `cmd_apply` itself only prints that CLI wiring for plan-file replay is a follow-up milestone and returns exit code 2. The Rust port keeps that same honest stub rather than inventing a `--plan-file` replay flow the Python CLI doesn't have. |
| `gui/app.py` + `gui/workers.py` | `waypoint-gui` (iced) | compiles; **not run** in this sandbox (no display server available to open a real window) — see deviations below |

## Real code, explicitly unvalidated (no way to check in this sandbox)

- **`platform/windows.py` → `waypoint-platform::windows`** (WMI-based, `wmi` crate). Shaped to match the Python implementation's `Win32_PnPEntity`/`Win32_PnPSignedDriver` queries and `CreateRestorePoint` WMI method call. `cfg(target_os = "windows")`-gated out of every build in this sandbox — there is no Windows toolchain here to compile-check it, let alone run it against real hardware. Treat as a first draft that needs a real Windows compile + test pass before trusting it.
- **`sources/windows_update.py` → `waypoint-sources::windows_update`** (`WindowsUpdateCatalogSource`). Same caveat. `search()`/`fetch()` intentionally return descriptive "not yet implemented" errors rather than fabricated COM automation logic — Rust has no late-bound `IDispatch` equivalent to Python's `win32com`, so this needs deliberate design (e.g. the `windows` crate's typed COM bindings), not a naive port.

## Not ported (deferred, not faked)

- **`sources/oem/*.py`** (~770 lines: `cab.py`, `dell_catalog.py` 219 lines, `dell_driverpack.py` 139, `hp_platform.py` 122, `http.py` 32, `lenovo_driverpack.py` 120, `model_pack.py` 51, `__init__.py` 24). Dell's `CatalogPC.cab` parsing (CAB extraction + XML) and the HTTP client layer for Lenovo/HP catalogs deserve dedicated attention rather than a rushed port in the same pass as the rest of the scaffold. `engine::factory::build_oem_sources()` does not exist yet in Rust — calling `waypoint-cli --oem` returns a hard error explaining this, rather than silently scanning without OEM coverage a fleet script might assume it has.
- **CLI `--oem` / `--force-oem-refresh` wiring** — flags exist on `waypoint-cli` for interface parity with the Python CLI, but `build_engine()` returns an error if `--oem` is passed, for the same reason as above.
- **GUI OEM checkbox** — present in `waypoint-gui` for interface parity with the PySide6 version's checkbox, but permanently disabled/unwired — there is nothing yet for it to enable.

## Known, documented deviation: GUI threading model

The Python GUI (`gui/workers.py`) runs `WaypointEngine.scan()` on a
background `QThread` specifically so a slow WMI enumeration or an OEM
catalog download can't freeze the window. The iced port
(`waypoint-gui`) runs `scan()` synchronously on the button-click handler
instead.

This was a deliberate choice, not an oversight: doing it properly requires
`Box<dyn DeviceBackend>` to be provably `Send` across an async task
boundary so it can move into an `iced::Task`, and the trait doesn't
currently guarantee that. Rather than add an unverified `unsafe impl Send`
or fake responsiveness, the synchronous call was kept and documented. Not
noticeable with the mock or local udev backends (both fast, local calls);
would be noticeable on a real Windows fleet scan or an OEM catalog
download once those exist. Fixing this properly is future scope: either
add a `Send` bound to `DeviceBackend`, or wrap the engine in
`Arc<Mutex<...>>` before wiring a background task.

## Environment notes for whoever builds this next

- Needs `libudev-dev` installed (`apt-get install libudev-dev pkg-config`) for `waypoint-platform`'s Linux backend to compile — `libudev-sys`'s build script shells out to `pkg-config --libs --cflags libudev`.
- `time` crate needs its `serde` feature enabled workspace-wide (`time::Date` doesn't implement `Serialize`/`Deserialize` without it) — already set in the workspace `Cargo.toml`, but easy to lose if dependencies get re-pinned.
- `windows.rs` is real code but has never been compiled anywhere — building it for the first time on an actual Windows machine should be treated as "first draft," not "should just work."
