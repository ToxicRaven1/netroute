//! Configuration: the list of interfaces and the process-to-interface map.
//!
//! The config is TOML. You can add a new interface or a new route with a few
//! lines. You do not change the code.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;

/// The kind of an interface. The engine treats all kinds the same way.
/// The kind is only a label for the GUI and for your own notes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum InterfaceKind {
    /// A physical adapter, such as the built-in Wi-Fi or a USB Wi-Fi adapter.
    Physical,
    /// A VPN adapter, such as the TorGuard adapter.
    /// It is just another interface. It is not a special case.
    Vpn,
}

/// One network path.
///
/// `adapter` is the friendly name that Windows shows for the adapter, for
/// example "Wi-Fi" or "Wi-Fi 2" or "TorGuard-TAP". The engine resolves this
/// name to a live ifindex, local IP, and gateway at start and again after a
/// reconnect. See `interfaces.rs`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InterfaceCfg {
    /// A short id that you choose. Routes point at this id.
    pub id: String,
    /// The Windows friendly name of the adapter.
    pub adapter: String,
    /// The kind label.
    pub kind: InterfaceKind,
}

/// One routing rule: send this process out this interface.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RouteCfg {
    /// The process image name, for example "firefox.exe".
    /// The match is on the image name only, not the full path. It ignores case.
    /// Two instances of the same exe cannot split. Use two different browsers.
    pub process: String,
    /// The interface id that this process must use.
    pub interface: String,
}

/// The whole config file.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Config {
    /// The interface id for any process that has no route.
    /// Keep this on your controlled Wi-Fi so new apps are safe by default.
    pub default_interface: String,
    #[serde(rename = "interface")]
    pub interfaces: Vec<InterfaceCfg>,
    #[serde(rename = "route", default)]
    pub routes: Vec<RouteCfg>,
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("cannot read config file: {0}")]
    Read(#[from] std::io::Error),
    #[error("cannot parse config file: {0}")]
    Parse(#[from] toml::de::Error),
    #[error("default_interface '{0}' is not in the interface list")]
    UnknownDefault(String),
    #[error("route for '{process}' points at unknown interface '{interface}'")]
    UnknownRouteInterface { process: String, interface: String },
    #[error("duplicate interface id '{0}'")]
    DuplicateId(String),
}

impl Config {
    /// Read and check a config file.
    pub fn load(path: &Path) -> Result<Self, ConfigError> {
        let text = std::fs::read_to_string(path)?;
        let cfg: Config = toml::from_str(&text)?;
        cfg.validate()?;
        Ok(cfg)
    }

    /// Check that the config is consistent before the engine uses it.
    /// A bad config must stop the program early, not mid-flow.
    pub fn validate(&self) -> Result<(), ConfigError> {
        let mut ids = HashMap::new();
        for i in &self.interfaces {
            if ids.insert(i.id.as_str(), ()).is_some() {
                return Err(ConfigError::DuplicateId(i.id.clone()));
            }
        }
        if !ids.contains_key(self.default_interface.as_str()) {
            return Err(ConfigError::UnknownDefault(self.default_interface.clone()));
        }
        for r in &self.routes {
            if !ids.contains_key(r.interface.as_str()) {
                return Err(ConfigError::UnknownRouteInterface {
                    process: r.process.clone(),
                    interface: r.interface.clone(),
                });
            }
        }
        Ok(())
    }

    /// Build a fast lookup from lower-case process name to interface id.
    pub fn route_map(&self) -> HashMap<String, String> {
        self.routes
            .iter()
            .map(|r| (r.process.to_ascii_lowercase(), r.interface.clone()))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> &'static str {
        r#"
            default_interface = "wifi-proxy"

            [[interface]]
            id = "wifi-proxy"
            adapter = "Wi-Fi"
            kind = "physical"

            [[interface]]
            id = "hotspot"
            adapter = "Wi-Fi 2"
            kind = "physical"

            [[interface]]
            id = "torguard"
            adapter = "TorGuard-TAP"
            kind = "vpn"

            [[route]]
            process = "firefox.exe"
            interface = "hotspot"

            [[route]]
            process = "chrome.exe"
            interface = "wifi-proxy"
        "#
    }

    #[test]
    fn parses_and_validates() {
        let cfg: Config = toml::from_str(sample()).unwrap();
        cfg.validate().unwrap();
        assert_eq!(cfg.interfaces.len(), 3);
        let map = cfg.route_map();
        assert_eq!(map.get("firefox.exe").unwrap(), "hotspot");
    }

    #[test]
    fn rejects_unknown_default() {
        // Point default_interface at an id that no interface defines.
        let bad = sample().replace(
            r#"default_interface = "wifi-proxy""#,
            r#"default_interface = "ghost""#,
        );
        let cfg: Config = toml::from_str(&bad).unwrap();
        assert!(matches!(cfg.validate(), Err(ConfigError::UnknownDefault(_))));
    }

    #[test]
    fn rejects_route_to_unknown_interface() {
        let bad = sample().replace(r#"interface = "hotspot""#, r#"interface = "ghost""#);
        let cfg: Config = toml::from_str(&bad).unwrap();
        assert!(matches!(
            cfg.validate(),
            Err(ConfigError::UnknownRouteInterface { .. })
        ));
    }
}
