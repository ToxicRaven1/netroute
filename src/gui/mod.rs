//! The GUI.
//!
//! The look is a classic Win32 program. `native-windows-gui` uses real common
//! controls, so the window, the list view, the buttons, and the status bar all
//! take the system theme. On a classic theme they look like a legacy program.
//! Do not add a modern flat theme. Let the controls use the system style.
//!
//! The window has three parts:
//!   - A status area at the top. One row per interface: id, adapter, up or down,
//!     and the live IP. It refreshes on a timer and on an adapter change.
//!   - A list in the middle. One row per route: process, interface. The user
//!     can add, edit, or remove a row. A change updates the engine route map at
//!     once; no restart.
//!   - A status bar at the bottom: flow count and NAT entry count.
//!
//! The GUI never touches packets. It only reads engine state and edits the
//! route map behind the engine's lock. That keeps the data path and the UI
//! apart.

use crate::engine::Engine;
use std::sync::Arc;

/// Run the GUI event loop. Returns when the user closes the window.
#[cfg(windows)]
pub fn run(engine: Arc<Engine>) -> anyhow::Result<()> {
    // Build plan with native-windows-gui (nwg):
    //
    //  nwg::init()?;
    //  let mut window = Default::default();
    //  nwg::Window::builder()
    //      .size((640, 480))
    //      .title("netroute")
    //      .build(&mut window)?;
    //
    //  - A ListView for interfaces (read-only).
    //  - A ListView for routes (editable via Add / Edit / Remove buttons).
    //  - A StatusBar for counts.
    //  - A nwg::AnimationTimer every 1000 ms to refresh status from `engine`.
    //
    // On "Add route": read process name and interface id from small input
    // dialogs, then:
    //     engine.route_map.write().insert(name.to_ascii_lowercase(), iface_id);
    // On "Remove route": remove the key. The change takes effect on the next
    // packet. No restart.
    //
    // On the refresh timer: for each `engine.interfaces.read().iter()` set the
    // row text and colour the "up/down" cell. Read `engine.flows.len()` and
    // `engine.nat.len()` for the status bar.
    //
    // nwg::dispatch_thread_events();  // blocks until the window closes
    let _ = engine;
    anyhow::bail!("gui::run: build the native-windows-gui window — see the plan in this function")
}

#[cfg(not(windows))]
pub fn run(_engine: Arc<Engine>) -> anyhow::Result<()> {
    anyhow::bail!("the gui runs on Windows only")
}
