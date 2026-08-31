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
| `sources/oem/dell_catalog.py` | `waypoint-sources::oem::dell_catalog` (`DellCatalogSource`) | 4/4 ported unit tests pass (`crates/sources/tests/oem_tests.rs`, part of the full port of `tests/test_oem_sources.py`); additionally exercised against Dell's real, live `CatalogPC.cab` via `cargo run -p waypoint-cli -- --oem scan --json` (real ~57MB download, cab extraction via system `cabextract`, UTF-16 decode, XML parse, search — all against production data, not just fixtures). Wired into `engine::factory::build_oem_sources()`, the CLI's `--oem`/`--force-oem-refresh` flags, and the GUI's OEM checkbox — the only OEM source actually reachable from a scan, matching Python's own `build_oem_sources()` scope exactly. |

## Real code, explicitly unvalidated (no way to check in this sandbox)

- **`platform/windows.py` → `waypoint-platform::windows`** (WMI-based, `wmi` crate). Shaped to match the Python implementation's `Win32_PnPEntity`/`Win32_PnPSignedDriver` queries and `CreateRestorePoint` WMI method call. `cfg(target_os = "windows")`-gated out of every build in this sandbox — there is no Windows toolchain here to compile-check it, let alone run it against real hardware. Treat as a first draft that needs a real Windows compile + test pass before trusting it.
- **`sources/windows_update.py` → `waypoint-sources::windows_update`** (`WindowsUpdateCatalogSource`). Same caveat. `search()`/`fetch()` intentionally return descriptive "not yet implemented" errors rather than fabricated COM automation logic — Rust has no late-bound `IDispatch` equivalent to Python's `win32com`, so this needs deliberate design (e.g. the `windows` crate's typed COM bindings), not a naive port.

## Partially ported

- **`sources/oem/*.py`** (~770 lines: `cab.py`, `dell_catalog.py` 219 lines, `dell_driverpack.py` 139, `hp_platform.py` 122, `http.py` 32, `lenovo_driverpack.py` 120, `model_pack.py` 51, `__init__.py` 24). All four catalog sources are now ported as real, tested Rust library code under `waypoint-sources::oem` (`crates/sources/src/oem/`: `dell_catalog.rs`, `dell_driverpack.rs`, `lenovo_driverpack.rs`, `hp_platform.rs`, plus shared `http.rs`/`cab.rs`/`model_pack.rs`), with `crates/sources/tests/oem_tests.rs` porting all 10 tests from `tests/test_oem_sources.py` — all pass. But only **`DellCatalogSource`** (a `DriverSource`) is actually reachable from a scan: `engine::factory::build_oem_sources()` returns just `vec![DellCatalogSource]`, exactly mirroring Python's own `build_oem_sources()` in `engine/factory.py`, which never wires in the driverpack/platform-list sources either — not a gap introduced by this port. `DellDriverPackSource`, `LenovoDriverPackSource`, and `HpPlatformCatalogSource` compile, are unit-tested, and are callable directly as library code, but are not reachable from any CLI flag, GUI control, or `build_oem_sources()` call in *either* language. Closing that gap (adding a real driverpack-aware scan path) is new scope beyond parity, not a porting task.
- **CLI `--oem` / `--force-oem-refresh` wiring** — fully wired. `build_engine()` in `crates/cli/src/main.rs` calls `build_oem_sources()`, refreshes each source with `force_oem_refresh`, and extends `engine.sources` — direct port of `cli/main.py`'s `_build_engine()`. Verified with a real end-to-end run: `cargo run -q -p waypoint-cli -- --oem scan --json` downloaded and parsed Dell's live catalog successfully (exit code 0).
- **GUI OEM checkbox** — fully wired in `crates/gui/src/main.rs`. Checking it before Scan adds `build_oem_sources()` to that one scan's source list (refreshed per the force-refresh checkbox), then restores the original source list afterward — mirrors `ScanWorker.run()`'s try/finally sources-restore pattern in `gui/workers.py`, run synchronously instead of on a background thread per the documented threading deviation below. Not run against a real display server in this sandbox (same caveat as the rest of `waypoint-gui`).
- **CLI-level `--oem` integration tests (`tests/test_cli_oem_flag.py`)** — NOT ported. The Python tests rely on `monkeypatch.setattr("waypoint.engine.factory.build_default_backend", ...)` to inject a `MockDeviceBackend` into a real `main()` invocation; Rust has no equivalent to swap a free function out from under a compiled binary. Porting these honestly would require adding a dependency-injection seam to `build_engine()`/`main()` first (e.g. an injectable backend parameter) — a small design change, not a straight port, and out of scope for this pass. The underlying behavior these tests check (source wiring, refresh force/no-force, cache reuse) is covered instead by the unit tests in `oem_tests.rs` plus the real end-to-end CLI smoke test noted above.

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
