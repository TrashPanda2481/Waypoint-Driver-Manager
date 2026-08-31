//! Thin binary entry point. All real logic lives in the `waypoint_cli`
//! library crate (`src/lib.rs`) so the test suite can call the same code
//! with an injected backend and captured output instead of spawning real
//! processes or hitting real hardware — see that module's doc comment
//! for why.

use std::process::ExitCode;

use clap::Parser;
use waypoint_cli::{run, Cli};

fn main() -> ExitCode {
    let cli = Cli::parse();
    ExitCode::from(run(cli, &mut std::io::stdout(), &mut std::io::stderr()))
}
