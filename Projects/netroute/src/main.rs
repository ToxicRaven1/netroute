//! netroute — per-process traffic routing across interfaces on Windows.
//!
//! Start order:
//!   1. Read and check the config.
//!   2. Enumerate adapters and resolve the config to live interfaces.
//!   3. Build the engine.
//!   4. Start the FLOW reader and the NETWORK worker.
//!   5. Start the GUI. The GUI reads engine state and edits the route map.
//!
//! The program needs Administrator rights. WinDivert loads a driver, which
//! needs elevation. Ship a manifest that asks for "requireAdministrator" so the
//! user gets the elevation prompt at launch instead of a silent failure.

mod config;
mod engine;
mod gui;
mod interfaces;

use anyhow::Context;
use std::path::PathBuf;
use std::sync::Arc;

fn main() -> anyhow::Result<()> {
    env_logger::init();

    let cfg_path = config_path();
    let cfg = config::Config::load(&cfg_path)
        .with_context(|| format!("loading config from {}", cfg_path.display()))?;
    log::info!("config ok: {} interfaces, {} routes", cfg.interfaces.len(), cfg.routes.len());

    let adapters = interfaces::enumerate_adapters().context("reading network adapters")?;
    let table = interfaces::resolve(&cfg.interfaces, &adapters);
    for i in table.iter() {
        log::info!("interface {} -> adapter '{}' up={} ifindex={}", i.id, i.adapter, i.is_up, i.if_index);
    }

    let engine = Arc::new(engine::Engine::new(&cfg, table));
    engine.running.store(true, std::sync::atomic::Ordering::SeqCst);

    // Start the capture threads. On a machine without the driver these return
    // an error; surface it in the GUI rather than crashing.
    #[cfg(windows)]
    {
        let _flow = engine::spawn_flow_reader(engine.clone()).context("starting flow reader")?;
        let _net = engine::spawn_network_worker(engine.clone()).context("starting network worker")?;
        // A background sweep keeps the NAT table bounded for UDP flows.
        {
            let nat = engine.nat.clone();
            std::thread::spawn(move || loop {
                std::thread::sleep(std::time::Duration::from_secs(10));
                let dropped = nat.sweep();
                if dropped > 0 {
                    log::debug!("nat sweep dropped {dropped} idle flows");
                }
            });
        }
    }

    gui::run(engine).context("starting gui")?;
    Ok(())
}

/// Look for the config next to the executable first, then in the current dir.
/// Keep it simple: no registry, no AppData, so the user can see and edit it.
fn config_path() -> PathBuf {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let p = dir.join("config.toml");
            if p.exists() {
                return p;
            }
        }
    }
    PathBuf::from("config.toml")
}
