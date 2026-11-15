//! Live interface resolution.
//!
//! The config names an adapter by its Windows friendly name. The engine needs
//! live facts about that adapter: the ifindex, the local IPv4 address, and the
//! gateway. These facts change. The adapter can reconnect and get a new IP. The
//! VPN can go up or down. So the engine resolves the facts at start and again
//! on demand after a change.

use std::collections::HashMap;
use std::net::Ipv4Addr;

/// The live facts about one adapter, resolved from the config `adapter` name.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedInterface {
    /// The config id, copied here for convenience.
    pub id: String,
    /// The adapter friendly name that was resolved.
    pub adapter: String,
    /// The IPv4 interface index. WinDivert reinjection uses this.
    pub if_index: u32,
    /// The local IPv4 address. The engine rewrites the packet source to this.
    pub local_ip: Ipv4Addr,
    /// True when the adapter is up and has a usable IPv4 address.
    pub is_up: bool,
}

/// The full set of resolved interfaces, keyed by config id.
#[derive(Debug, Clone, Default)]
pub struct InterfaceTable {
    by_id: HashMap<String, ResolvedInterface>,
}

impl InterfaceTable {
    pub fn get(&self, id: &str) -> Option<&ResolvedInterface> {
        self.by_id.get(id)
    }

    pub fn is_up(&self, id: &str) -> bool {
        self.by_id.get(id).map(|i| i.is_up).unwrap_or(false)
    }

    pub fn iter(&self) -> impl Iterator<Item = &ResolvedInterface> {
        self.by_id.values()
    }
}

/// A snapshot of one adapter as read from the operating system.
/// This is the OS-level fact, before it is matched to a config id.
#[derive(Debug, Clone)]
pub struct AdapterSnapshot {
    pub friendly_name: String,
    pub if_index: u32,
    pub ipv4: Option<Ipv4Addr>,
    pub oper_up: bool,
}

/// Read every adapter from the operating system.
///
/// On Windows this calls GetAdaptersAddresses from the IP Helper API and reads,
/// for each adapter: the friendly name, the IPv4 interface index
/// (`IfIndex` from the unicast address or the adapter struct), the first unicast
/// IPv4 address, and the operational status.
///
/// The Windows FFI below is the one place that needs a build machine to verify.
/// The `windows` crate exposes `GetAdaptersAddresses`,
/// `IP_ADAPTER_ADDRESSES_LH`, and `IP_ADAPTER_UNICAST_ADDRESS_LH`. Confirm the
/// exact field and function names against the crate version in `Cargo.toml`
/// before you trust this compiles.
#[cfg(windows)]
pub fn enumerate_adapters() -> anyhow::Result<Vec<AdapterSnapshot>> {
    // Implementation plan (fill in against the `windows` crate on the build box):
    //
    // 1. Call GetAdaptersAddresses(AF_INET, flags, null, buf, &size) once with a
    //    null buffer to learn the size, then again with a buffer of that size.
    //    Loop while it returns ERROR_BUFFER_OVERFLOW and grow the buffer.
    // 2. Walk the IP_ADAPTER_ADDRESSES_LH linked list via the `Next` pointer.
    // 3. For each adapter:
    //    - friendly_name: from the `FriendlyName` wide string.
    //    - if_index:      from `IfIndex` (IPv4 index).
    //    - oper_up:       `OperStatus == IfOperStatusUp`.
    //    - ipv4:          walk the `FirstUnicastAddress` list, take the first
    //                     AF_INET sockaddr, read its 4 bytes.
    //
    // Keep all unsafe pointer walking inside this function. Return only the safe
    // AdapterSnapshot values so the rest of the program stays safe code.
    anyhow::bail!("enumerate_adapters: Windows FFI not yet filled in — see the plan in this function")
}

/// A stub so the crate type-checks on non-Windows machines (for example this
/// design environment). It never runs on the target.
#[cfg(not(windows))]
pub fn enumerate_adapters() -> anyhow::Result<Vec<AdapterSnapshot>> {
    anyhow::bail!("this program runs on Windows only")
}

/// Match OS adapters to the config and build the resolved table.
///
/// This is pure logic over `enumerate_adapters`, so it is testable without
/// Windows by passing a snapshot list in.
pub fn resolve(
    interfaces: &[crate::config::InterfaceCfg],
    adapters: &[AdapterSnapshot],
) -> InterfaceTable {
    let mut by_id = HashMap::new();
    for cfg in interfaces {
        // Match on the friendly name, ignoring case and surrounding spaces.
        let found = adapters.iter().find(|a| {
            a.friendly_name.trim().eq_ignore_ascii_case(cfg.adapter.trim())
        });
        if let Some(a) = found {
            let is_up = a.oper_up && a.ipv4.is_some();
            by_id.insert(
                cfg.id.clone(),
                ResolvedInterface {
                    id: cfg.id.clone(),
                    adapter: a.friendly_name.clone(),
                    if_index: a.if_index,
                    local_ip: a.ipv4.unwrap_or(Ipv4Addr::UNSPECIFIED),
                    is_up,
                },
            );
        } else {
            // The adapter is not present now. Record it as down so the GUI can
            // show "waiting" instead of the program failing.
            by_id.insert(
                cfg.id.clone(),
                ResolvedInterface {
                    id: cfg.id.clone(),
                    adapter: cfg.adapter.clone(),
                    if_index: 0,
                    local_ip: Ipv4Addr::UNSPECIFIED,
                    is_up: false,
                },
            );
        }
    }
    InterfaceTable { by_id }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{InterfaceCfg, InterfaceKind};

    #[test]
    fn resolves_present_and_absent() {
        let cfgs = vec![
            InterfaceCfg { id: "hotspot".into(), adapter: "Wi-Fi 2".into(), kind: InterfaceKind::Physical },
            InterfaceCfg { id: "vpn".into(), adapter: "TorGuard-TAP".into(), kind: InterfaceKind::Vpn },
        ];
        let adapters = vec![AdapterSnapshot {
            friendly_name: "wi-fi 2".into(), // different case on purpose
            if_index: 17,
            ipv4: Some(Ipv4Addr::new(192, 168, 43, 50)),
            oper_up: true,
        }];
        let table = resolve(&cfgs, &adapters);
        let hs = table.get("hotspot").unwrap();
        assert!(hs.is_up);
        assert_eq!(hs.if_index, 17);
        assert_eq!(hs.local_ip, Ipv4Addr::new(192, 168, 43, 50));
        // VPN is down because no adapter matched.
        assert!(!table.is_up("vpn"));
    }
}
