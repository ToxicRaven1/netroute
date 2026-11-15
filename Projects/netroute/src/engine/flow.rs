//! Flow tracking: learn which process owns each connection.
//!
//! WinDivert's FLOW layer sends an event when a connection opens and when it
//! closes. Each event carries the PID and the 5-tuple. The engine listens on
//! that layer and keeps a table from 5-tuple to PID and process name. The
//! NETWORK layer then looks a packet up in this table to find its process.
//!
//! This table is pure logic. The FLOW-layer reader in `mod.rs` feeds it.

use parking_lot::RwLock;
use std::collections::HashMap;
use std::net::Ipv4Addr;
use std::sync::Arc;

/// The 5-tuple that names one connection.
/// The protocol, both addresses, and both ports.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct FiveTuple {
    pub protocol: u8, // 6 = TCP, 17 = UDP
    pub local_addr: Ipv4Addr,
    pub local_port: u16,
    pub remote_addr: Ipv4Addr,
    pub remote_port: u16,
}

/// What the engine knows about one flow.
#[derive(Debug, Clone)]
pub struct FlowInfo {
    pub pid: u32,
    /// Lower-case process image name, for example "firefox.exe".
    pub process: String,
}

/// A shared, lock-guarded flow table.
#[derive(Clone, Default)]
pub struct FlowTable {
    inner: Arc<RwLock<HashMap<FiveTuple, FlowInfo>>>,
}

impl FlowTable {
    pub fn new() -> Self {
        Self::default()
    }

    /// A new connection opened. Record its owner.
    pub fn insert(&self, key: FiveTuple, info: FlowInfo) {
        self.inner.write().insert(key, info);
    }

    /// A connection closed. Drop it so the table does not grow without limit.
    pub fn remove(&self, key: &FiveTuple) {
        self.inner.write().remove(key);
    }

    /// Find the owner of a flow. The NETWORK layer calls this per packet, so it
    /// takes a read lock only and clones the small result.
    pub fn lookup(&self, key: &FiveTuple) -> Option<FlowInfo> {
        self.inner.read().get(key).cloned()
    }

    pub fn len(&self) -> usize {
        self.inner.read().len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn insert_lookup_remove() {
        let t = FlowTable::new();
        let key = FiveTuple {
            protocol: 6,
            local_addr: Ipv4Addr::new(192, 168, 1, 10),
            local_port: 52344,
            remote_addr: Ipv4Addr::new(140, 82, 112, 3),
            remote_port: 443,
        };
        t.insert(key, FlowInfo { pid: 4242, process: "firefox.exe".into() });
        assert_eq!(t.lookup(&key).unwrap().process, "firefox.exe");
        t.remove(&key);
        assert!(t.lookup(&key).is_none());
    }
}
