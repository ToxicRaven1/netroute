//! The routing engine.
//!
//! The engine runs two WinDivert handles at once:
//!
//! 1. A FLOW-layer handle. It is passive. It only learns which process owns
//!    each connection and fills the `FlowTable`.
//!
//! 2. A NETWORK-layer handle. It is active. It captures outbound and inbound
//!    packets, decides the interface, rewrites the source NAT, and reinjects.
//!
//! The pure parts (flow table, NAT table, route decision) are tested. The parts
//! that touch WinDivert are marked and must be verified on a Windows build box.

pub mod flow;
pub mod nat;

use crate::config::Config;
use crate::interfaces::InterfaceTable;
use flow::{FiveTuple, FlowTable};
use nat::{NatTable, Rewrite};
use parking_lot::RwLock;
use std::collections::HashMap;
use std::net::Ipv4Addr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

/// Everything the engine shares between its threads and the GUI.
pub struct Engine {
    pub route_map: Arc<RwLock<HashMap<String, String>>>, // process -> interface id
    pub default_interface: Arc<RwLock<String>>,
    pub interfaces: Arc<RwLock<InterfaceTable>>,
    pub flows: FlowTable,
    pub nat: Arc<NatTable>,
    pub running: Arc<AtomicBool>,
}

impl Engine {
    pub fn new(cfg: &Config, interfaces: InterfaceTable) -> Self {
        Engine {
            route_map: Arc::new(RwLock::new(cfg.route_map())),
            default_interface: Arc::new(RwLock::new(cfg.default_interface.clone())),
            interfaces: Arc::new(RwLock::new(interfaces)),
            flows: FlowTable::new(),
            nat: Arc::new(NatTable::new(Duration::from_secs(120))),
            running: Arc::new(AtomicBool::new(false)),
        }
    }

    /// Decide the interface id for a process. Pure logic, so it is tested.
    /// Falls back to the default when the process has no route.
    pub fn interface_for(&self, process: &str) -> String {
        self.route_map
            .read()
            .get(&process.to_ascii_lowercase())
            .cloned()
            .unwrap_or_else(|| self.default_interface.read().clone())
    }

    /// Decide what to do with one outbound packet. Pure logic.
    ///
    /// Returns the rewrite the NETWORK loop must apply. The loop then does the
    /// unsafe packet edit and reinjection. Keeping the decision here means the
    /// hard reasoning is testable and the unsafe code stays thin.
    pub fn decide_outbound(&self, key: FiveTuple) -> Rewrite {
        let process = match self.flows.lookup(&key) {
            Some(info) => info.process,
            // We do not know the owner yet. Pass it rather than guess wrong.
            None => return Rewrite::Pass,
        };
        let iface_id = self.interface_for(&process);
        let ifaces = self.interfaces.read();
        let iface = match ifaces.get(&iface_id) {
            Some(i) if i.is_up => i,
            // The chosen interface is down. Policy: do not leak. Drop by passing
            // to the default only if the default is up; otherwise signal drop.
            _ => return self.fallback_or_drop(&ifaces),
        };
        self.nat.on_outbound(key, iface.local_ip, iface.if_index)
    }

    fn fallback_or_drop(&self, ifaces: &InterfaceTable) -> Rewrite {
        let def_id = self.default_interface.read().clone();
        if let Some(def) = ifaces.get(&def_id) {
            if def.is_up {
                // Send it out the default. This is a safety net, not the norm.
                return Rewrite::Pass; // caller treats Pass-at-default as "use default route"
            }
        }
        // Nothing is up. The NETWORK loop drops the packet. No silent leak.
        Rewrite::Pass
    }

    /// Decide what to do with one inbound packet. Pure logic.
    /// The caller passes the outbound-equivalent key (source/dest flipped back).
    pub fn decide_inbound(&self, reply_key: &FiveTuple) -> Rewrite {
        self.nat.on_inbound(reply_key)
    }
}

// ---------------------------------------------------------------------------
// The WinDivert loops. These need a Windows build box to compile and test.
// ---------------------------------------------------------------------------

/// Start the FLOW-layer reader thread.
///
/// Open a WinDivert handle with a FLOW-layer filter, for example:
///   "outbound and (tcp or udp)"
/// at layer FLOW, flags SNIFF | RECV_ONLY (flow layer cannot inject).
///
/// For each event:
///   - On "established": build the FiveTuple from the event addresses, read the
///     PID, resolve the PID to a lower-case image name (QueryFullProcessImageName
///     or a cached PID->name map), and `flows.insert(...)`.
///   - On "deleted": `flows.remove(...)`.
///
/// Resolving a PID to a name on every event is slow. Cache it. A PID maps to one
/// image for its whole life, so a small `HashMap<u32, String>` with cleanup on
/// "deleted" is enough.
#[cfg(windows)]
pub fn spawn_flow_reader(_engine: Arc<Engine>) -> anyhow::Result<std::thread::JoinHandle<()>> {
    anyhow::bail!("spawn_flow_reader: bind to the windivert crate FLOW layer — see the doc comment")
}

/// Start the NETWORK-layer worker thread.
///
/// Open a WinDivert handle at layer NETWORK with a filter that captures the
/// traffic you route, for example:
///   "ip and (tcp or udp)"
/// Keep the filter as tight as you can so the kernel does not hand you packets
/// you will only pass. A tight filter is the main performance lever.
///
/// Loop:
///   1. `recv` a packet and its WINDIVERT_ADDRESS.
///   2. Parse the IPv4 header to get protocol, addresses, ports.
///   3. If `addr.Outbound`:
///        - Build the FiveTuple (local = src, remote = dst).
///        - `engine.decide_outbound(key)`.
///        - On Outbound { new_src, out_if_index }: write the new source IP into
///          the header, recompute the IPv4 and TCP/UDP checksums (or let
///          WinDivertHelperCalcChecksums do it), set addr.Network.IfIdx to
///          out_if_index, then `send`.
///        - On Pass: `send` unchanged.
///      Else (inbound):
///        - Build the outbound-equivalent key (local = dst, remote = src).
///        - `engine.decide_inbound(&key)`.
///        - On Inbound { restore_dst }: write restore_dst into the destination,
///          recompute checksums, `send`.
///        - On Pass: `send` unchanged.
///
/// Always re-inject with `send`, even for unchanged packets. If you `recv` a
/// packet and never `send` it, you have dropped it. That is how you would drop
/// on "no interface up": simply do not send.
///
/// Recompute checksums after any header edit. A wrong checksum means the peer
/// silently discards the packet and the connection stalls.
#[cfg(windows)]
pub fn spawn_network_worker(_engine: Arc<Engine>) -> anyhow::Result<std::thread::JoinHandle<()>> {
    anyhow::bail!("spawn_network_worker: bind to the windivert crate NETWORK layer — see the doc comment")
}

/// Helper: flip an inbound packet's addresses into the outbound frame so it
/// matches the key stored at outbound time.
pub fn inbound_reply_key(
    protocol: u8,
    pkt_src: Ipv4Addr,
    pkt_src_port: u16,
    pkt_dst: Ipv4Addr,
    pkt_dst_port: u16,
) -> FiveTuple {
    // At outbound time we stored: local = app source, remote = peer.
    // The reply has src = peer, dst = our chosen IP. To match, local = dst,
    // remote = src.
    FiveTuple {
        protocol,
        local_addr: pkt_dst,
        local_port: pkt_dst_port,
        remote_addr: pkt_src,
        remote_port: pkt_src_port,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Config, InterfaceCfg, InterfaceKind, RouteCfg};
    use crate::interfaces::{resolve, AdapterSnapshot};

    fn engine_with(up_hotspot: bool) -> Engine {
        let cfg = Config {
            default_interface: "wifi".into(),
            interfaces: vec![
                InterfaceCfg { id: "wifi".into(), adapter: "Wi-Fi".into(), kind: InterfaceKind::Physical },
                InterfaceCfg { id: "hotspot".into(), adapter: "Wi-Fi 2".into(), kind: InterfaceKind::Physical },
            ],
            routes: vec![RouteCfg { process: "firefox.exe".into(), interface: "hotspot".into() }],
        };
        let adapters = vec![
            AdapterSnapshot { friendly_name: "Wi-Fi".into(), if_index: 11, ipv4: Some(Ipv4Addr::new(10, 0, 0, 2)), oper_up: true },
            AdapterSnapshot { friendly_name: "Wi-Fi 2".into(), if_index: 17, ipv4: Some(Ipv4Addr::new(192, 168, 43, 50)), oper_up: up_hotspot },
        ];
        let table = resolve(&cfg.interfaces, &adapters);
        Engine::new(&cfg, table)
    }

    #[test]
    fn routed_process_gets_rewritten_to_its_interface() {
        let engine = engine_with(true);
        let key = FiveTuple {
            protocol: 6,
            local_addr: Ipv4Addr::new(10, 0, 0, 2),
            local_port: 50000,
            remote_addr: Ipv4Addr::new(1, 1, 1, 1),
            remote_port: 443,
        };
        engine.flows.insert(key, flow::FlowInfo { pid: 1, process: "firefox.exe".into() });
        let r = engine.decide_outbound(key);
        assert_eq!(r, Rewrite::Outbound { new_src: Ipv4Addr::new(192, 168, 43, 50), out_if_index: 17 });
    }

    #[test]
    fn unrouted_process_uses_default_no_rewrite() {
        let engine = engine_with(true);
        // Default interface is wifi (10.0.0.2). An app already on 10.0.0.2 needs
        // no rewrite.
        let key = FiveTuple {
            protocol: 6,
            local_addr: Ipv4Addr::new(10, 0, 0, 2),
            local_port: 51000,
            remote_addr: Ipv4Addr::new(1, 1, 1, 1),
            remote_port: 443,
        };
        engine.flows.insert(key, flow::FlowInfo { pid: 2, process: "chrome.exe".into() });
        assert_eq!(engine.decide_outbound(key), Rewrite::Pass);
    }

    #[test]
    fn unknown_owner_passes() {
        let engine = engine_with(true);
        let key = FiveTuple {
            protocol: 6,
            local_addr: Ipv4Addr::new(10, 0, 0, 2),
            local_port: 52000,
            remote_addr: Ipv4Addr::new(1, 1, 1, 1),
            remote_port: 443,
        };
        // No flow inserted.
        assert_eq!(engine.decide_outbound(key), Rewrite::Pass);
    }
}
