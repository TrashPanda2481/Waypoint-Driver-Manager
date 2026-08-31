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
| **New scope, no Python equivalent:** driverpack-aware scan path — `waypoint-platform::model` (`SystemModel`, `detect_system_model()`, Linux side via `/sys/class/dmi/id/*`), a Dell name-attribute fallback index added to `DellDriverPackSource::load_from_xml`, `waypoint-engine::factory::build_oem_model_pack_sources()`, and the CLI's `waypoint driverpack [--json] [--force-refresh]` subcommand (`cmd_driverpack`/`cmd_driverpack_with_model` in `crates/cli/src/lib.rs`) | 7/7 new tests pass: 1 in `crates/sources/tests/oem_tests.rs` (`test_dell_driverpack_falls_back_to_name_when_no_systemid_match`, confirms the fixture really does have two systemIDs sharing one name) + 6 in `crates/cli/tests/driverpack_tests.rs` (found-via-Dell-SKU, Dell-name-fallback, Lenovo-machine-type, HP-`is_supported`, none-found → exit code 1, and a partial-refresh-failure case where a corrupted cached HP file fails to parse but Dell/Lenovo still resolve and get reported — all against real fixture data, no network). Full workspace suite: 36/36 passing, no warnings. **Not run against real hardware in this sandbox** — no `/sys/class/dmi/id` exists in this container at all (confirmed directly), so the Linux detection path is real, compiling, fixture-tested code but genuinely unvalidated against an actual machine's SMBIOS data; validate on the Dell/Asus/other real hardware before trusting it the way `platform/linux.py`'s port was. Windows detection in `detect_system_model()` is `cfg(target_os = "windows")`-gated and carries the same unvalidated-first-draft caveat as `platform/windows.rs` below — no Windows toolchain here to even compile-check it. Not wired into the GUI yet — CLI only for now. |

## Real code, explicitly unvalidated (no way to check in this sandbox)

- **`platform/windows.py` → `waypoint-platform::windows`** (WMI-based, `wmi` crate). Shaped to match the Python implementation's `Win32_PnPEntity`/`Win32_PnPSignedDriver` queries and `CreateRestorePoint` WMI method call. `cfg(target_os = "windows")`-gated out of every build in this sandbox — there is no Windows toolchain here to compile-check it, let alone run it against real hardware. Treat as a first draft that needs a real Windows compile + test pass before trusting it.
- **`sources/windows_update.py` → `waypoint-sources::windows_update`** (`WindowsUpdateCatalogSource`). Same caveat. `search()`/`fetch()` intentionally return descriptive "not yet implemented" errors rather than fabricated COM automation logic — Rust has no late-bound `IDispatch` equivalent to Python's `win32com`, so this needs deliberate design (e.g. the `windows` crate's typed COM bindings), not a naive port.

## Partially ported

- **`sources/oem/*.py`** (~770 lines: `cab.py`, `dell_catalog.py` 219 lines, `dell_driverpack.py` 139, `hp_platform.py` 122, `http.py` 32, `lenovo_driverpack.py` 120, `model_pack.py` 51, `__init__.py` 24). All four catalog sources are now ported as real, tested Rust library code under `waypoint-sources::oem` (`crates/sources/src/oem/`: `dell_catalog.rs`, `dell_driverpack.rs`, `lenovo_driverpack.rs`, `hp_platform.rs`, plus shared `http.rs`/`cab.rs`/`model_pack.rs`), with `crates/sources/tests/oem_tests.rs` porting all 10 tests from `tests/test_oem_sources.py` — all pass. Only **`DellCatalogSource`** (a `DriverSource`) is reachable from `scan`/`plan`: `engine::factory::build_oem_sources()` returns just `vec![DellCatalogSource]`, exactly mirroring Python's own `build_oem_sources()` in `engine/factory.py`, which never wires in the driverpack/platform-list sources either — not a gap introduced by this port, and intentionally left that way since `scan`/`plan` are per-device, not per-model. **Update, this session: the CLI-reachability gap for `DellDriverPackSource`/`LenovoDriverPackSource`/`HpPlatformCatalogSource` is now closed** — not by adding them to `build_oem_sources()`/`scan` (they're the wrong shape for that, as above), but via a new, separate `waypoint driverpack` subcommand and `build_oem_model_pack_sources()` factory function — see the "New scope, no Python equivalent" row in the table above for full detail and test coverage. This was always new scope beyond parity (no Python reference implementation exists for a model-based lookup path either), not a porting task.
- **CLI `--oem` / `--force-oem-refresh` wiring** — fully wired. `build_engine_with_injected_backend()` in `crates/cli/src/lib.rs` (moved out of `main.rs` during the dependency-injection refactor below) calls `build_oem_sources()`, refreshes each source with `force_oem_refresh`, and extends `engine.sources` — direct port of `cli/main.py`'s `_build_engine()`. Verified with a real end-to-end run: `cargo run -q -p waypoint-cli -- --oem scan --json` downloaded and parsed Dell's live catalog successfully (exit code 0).
- **GUI OEM checkbox** — fully wired in `crates/gui/src/main.rs`. Checking it before Scan adds `build_oem_sources()` to that one scan's source list (refreshed per the force-refresh checkbox), then restores the original source list afterward — mirrors `ScanWorker.run()`'s try/finally sources-restore pattern in `gui/workers.py`, run synchronously instead of on a background thread per the documented threading deviation below. Not run against a real display server in this sandbox (same caveat as the rest of `waypoint-gui`).
- **CLI-level `--oem` integration tests (`tests/test_cli_oem_flag.py`)** — 6 of 8 ported directly; the remaining 2 ported as unit tests one layer down instead, for an honest reason explained below (not a gap).

  A dependency-injection seam was added specifically to make this possible, since Rust has no equivalent of `monkeypatch.setattr("...build_default_backend", ...)` to swap a free function out from under a compiled binary:
  - `engine::factory::build_engine_with_backend(backend, cache_dir, audit_log_path, min_signature)` — engine-level seam; `build_default_engine()` now delegates to it.
  - `waypoint-cli` was converted from a bin-only crate to lib+bin. All logic moved from `main.rs` into a new `crates/cli/src/lib.rs` (`main.rs` is now a 16-line wrapper): `pub struct Cli` / `pub enum Command`, `pub fn build_engine_with_injected_backend(...)`, `pub fn cmd_scan/cmd_plan/cmd_apply(..., out: &mut dyn Write)` (writing to an injectable sink instead of `println!`/`eprintln!`), and the top-level testable entry point `pub fn run_with_backend(cli, backend, out: &mut dyn Write, err: &mut dyn Write) -> u8`. `run()` resolves the real backend and calls `run_with_backend()`; `main()` calls `run()` with real stdout/stderr. Exit codes were changed from `std::process::ExitCode` (an opaque, non-inspectable type) to a plain `u8` throughout the library, matching Python's `main() -> int` — conversion to `std::process::ExitCode` happens only in `main.rs`, right before returning.

  Ported to `crates/cli/tests/oem_flag_tests.rs` using that seam (`MockDeviceBackend` injected directly, output captured into `Vec<u8>` buffers instead of `capsys`): `test_oem_flag_defaults_to_off`, `test_oem_flag_parses_before_subcommand`, `test_force_oem_refresh_flag_defaults_to_off`, `test_scan_without_oem_does_not_match_dell_only_hwid`, `test_scan_with_oem_flag_wires_dell_source`, `test_force_oem_refresh_without_oem_warns_but_does_not_error` — all 6 pass.

  The other 2 (`test_oem_cache_dir_is_reused_not_redownloaded`, `test_force_oem_refresh_forces_redownload_even_when_cached`) monkeypatch Python's module-level `download_file`/`extract_cab` functions, not the backend — a different seam at a different layer. `DellCatalogSource` already exposes exactly that seam (`set_downloader`/`set_extractor`, doc-commented as test-only hooks), so rather than plumbing a second downloader override through `build_oem_sources()` and the CLI purely to satisfy these 2 assertions — CLI-surface complexity no real caller needs — they were ported as unit tests directly against `DellCatalogSource::refresh()` in `crates/sources/tests/oem_tests.rs`: `test_dell_catalog_refresh_skips_download_when_cache_exists_and_not_forced` and `test_dell_catalog_refresh_redownloads_when_forced_even_if_cached`. Both pass, and close a real, previously-unfilled coverage gap — the existing `test_dell_catalog_fetch_verifies_md5_and_returns_sha256` only covered `fetch()`'s MD5-mismatch path, not `refresh()`'s cache-skip/force-redownload branches at all.

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
