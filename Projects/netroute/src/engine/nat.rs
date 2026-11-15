//! Per-process source NAT.
//!
//! This is the core of the program. Read the reason carefully.
//!
//! Windows picks the outbound adapter from the packet destination and the
//! global route table. It does not pick it from the source. So, to force an
//! app's traffic out a chosen adapter, the engine rewrites the packet source IP
//! to that adapter's IP and reinjects on that adapter's ifindex.
//!
//! That change breaks the return path. The reply comes back to the new source
//! IP. But the app's socket still waits for the reply at the original IP. So
//! the engine must also rewrite the reply back to the original IP before it
//! reaches the socket. That is source NAT, in both directions.
//!
//! This table remembers each rewrite so the engine can undo it on the way back.
//! It is pure logic and fully testable without Windows.

use crate::engine::flow::FiveTuple;
use parking_lot::Mutex;
use std::collections::HashMap;
use std::net::Ipv4Addr;
use std::sync::Arc;
use std::time::{Duration, Instant};

/// One remembered rewrite.
#[derive(Debug, Clone)]
struct NatEntry {
    /// The source IP the app really used (what we must restore on the reply).
    original_src: Ipv4Addr,
    /// The ifindex we sent it out on.
    out_if_index: u32,
    /// Last time we saw a packet for this flow. Used to expire idle entries.
    last_seen: Instant,
}

/// The direction the engine rewrites a packet.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Rewrite {
    /// Outbound: set the source IP to `new_src` and send out `out_if_index`.
    Outbound { new_src: Ipv4Addr, out_if_index: u32 },
    /// Inbound: set the destination IP back to `restore_dst`.
    Inbound { restore_dst: Ipv4Addr },
    /// No change. Let the packet pass as it is.
    Pass,
}

pub struct NatTable {
    inner: Arc<Mutex<HashMap<FiveTuple, NatEntry>>>,
    idle_timeout: Duration,
}

impl NatTable {
    pub fn new(idle_timeout: Duration) -> Self {
        Self { inner: Arc::new(Mutex::new(HashMap::new())), idle_timeout }
    }

    /// Handle an outbound packet.
    ///
    /// `key` is the flow as the app sees it (original source).
    /// `chosen_ip` and `chosen_if` are the interface the route map picked.
    ///
    /// If the source already equals the chosen IP, nothing needs to change.
    /// Otherwise record the original source and return an Outbound rewrite.
    pub fn on_outbound(
        &self,
        key: FiveTuple,
        chosen_ip: Ipv4Addr,
        chosen_if: u32,
    ) -> Rewrite {
        if key.local_addr == chosen_ip {
            return Rewrite::Pass;
        }
        let mut map = self.inner.lock();
        map.insert(
            key,
            NatEntry {
                original_src: key.local_addr,
                out_if_index: chosen_if,
                last_seen: Instant::now(),
            },
        );
        Rewrite::Outbound { new_src: chosen_ip, out_if_index: chosen_if }
    }

    /// Handle an inbound packet.
    ///
    /// The engine builds the lookup key by flipping the inbound packet into the
    /// outbound frame of reference: the reply's destination is our chosen IP,
    /// its source is the remote peer. We match the flow we rewrote and restore
    /// the original destination.
    ///
    /// `reply_key` must be built by the caller as the outbound-equivalent tuple
    /// (see `mod.rs`). If we have no record, the packet is not ours — pass it.
    pub fn on_inbound(&self, reply_key: &FiveTuple) -> Rewrite {
        let mut map = self.inner.lock();
        if let Some(entry) = map.get_mut(reply_key) {
            entry.last_seen = Instant::now();
            Rewrite::Inbound { restore_dst: entry.original_src }
        } else {
            Rewrite::Pass
        }
    }

    /// Drop idle entries. Call this on a timer from the engine.
    /// A TCP flow that ends also gets dropped by the FLOW close event, but UDP
    /// has no clean close, so the idle sweep is what bounds the table.
    pub fn sweep(&self) -> usize {
        let now = Instant::now();
        let mut map = self.inner.lock();
        let before = map.len();
        map.retain(|_, e| now.duration_since(e.last_seen) < self.idle_timeout);
        before - map.len()
    }

    pub fn len(&self) -> usize {
        self.inner.lock().len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tuple(src: Ipv4Addr) -> FiveTuple {
        FiveTuple {
            protocol: 6,
            local_addr: src,
            local_port: 50000,
            remote_addr: Ipv4Addr::new(140, 82, 112, 3),
            remote_port: 443,
        }
    }

    #[test]
    fn pass_when_source_already_correct() {
        let nat = NatTable::new(Duration::from_secs(60));
        let chosen = Ipv4Addr::new(192, 168, 43, 50);
        let r = nat.on_outbound(tuple(chosen), chosen, 17);
        assert_eq!(r, Rewrite::Pass);
        assert_eq!(nat.len(), 0);
    }

    #[test]
    fn rewrites_and_reverses() {
        let nat = NatTable::new(Duration::from_secs(60));
        let app_ip = Ipv4Addr::new(10, 0, 0, 5); // what the app bound to
        let chosen = Ipv4Addr::new(192, 168, 43, 50); // the hotspot IP
        let key = tuple(app_ip);

        let out = nat.on_outbound(key, chosen, 17);
        assert_eq!(out, Rewrite::Outbound { new_src: chosen, out_if_index: 17 });

        // The reply arrives. The caller builds the outbound-equivalent key,
        // which is the same `key` we stored.
        let back = nat.on_inbound(&key);
        assert_eq!(back, Rewrite::Inbound { restore_dst: app_ip });
    }

    #[test]
    fn sweep_drops_idle() {
        let nat = NatTable::new(Duration::from_millis(1));
        nat.on_outbound(tuple(Ipv4Addr::new(10, 0, 0, 5)), Ipv4Addr::new(192, 168, 43, 50), 17);
        std::thread::sleep(Duration::from_millis(5));
        assert_eq!(nat.sweep(), 1);
        assert_eq!(nat.len(), 0);
    }
}
