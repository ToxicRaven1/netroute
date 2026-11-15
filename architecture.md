# netroute — architecture

Per-process traffic routing across network interfaces on Windows, in Rust.

## Goal

Each app's traffic leaves on the interface you assign it. A VPN adapter counts
as just another interface, not a special case.

Topology:
- Interface 1: built-in Wi-Fi, on a controlled (proxy) network.
- Interface 2: a USB Wi-Fi adapter, on an isolated hotspot.
- Interface 3 (later): the TorGuard VPN adapter.

## Correction (2026-11-15): WinDivert cannot choose the egress adapter

The first design rewrote the packet source and reinjected it on a chosen
ifindex. This does not work. The official WinDivert 2.2 docs state that
`Network.IfIdx` is **ignored for outbound packets**. Windows routes a
locally-made packet by its **destination**. Both Wi-Fi and the hotspot reach the
whole internet, so both match the same default route; the lower-metric one wins.
A source rewrite does not change this.

The egress interface on Windows is chosen by **binding the sending socket** to
that interface, with the `IP_UNICAST_IF` socket option. Reinjection cannot do
it. Every real WinDivert split-tunnel tool redirects captured packets to a local
proxy, and the proxy sends them out on a bound socket.

So the two-way source-NAT design is dropped. The NAT and WinDivert NETWORK-layer
code are not used.

## Primary design: a local proxy per interface

Because the routed apps are **browsers**, and browsers speak proxy natively, the
reliable design needs no driver and no packet capture.

1. Run one small local proxy per interface (an HTTP CONNECT proxy is enough).
2. Each proxy binds its upstream sockets to its interface with `IP_UNICAST_IF`.
   In Rust: `socket2::Socket::bind_device_by_index_v4(NonZeroU32::new(if_index))`
   before `connect`. This forces the egress interface, whatever the route table
   says. The helper also fixes the network-byte-order rule for the IPv4 index.
3. Point each browser at its proxy:
   - Chrome / Edge: `--proxy-server=127.0.0.1:PORT` with a separate
     `--user-data-dir`.
   - Firefox: a dedicated profile with the proxy set.
4. The VPN is a proxy bound to the TorGuard TAP adapter. Same code path.

Return traffic needs no NAT. The proxy holds both sockets, so replies come back
on the bound socket by themselves.

Benefits over the WinDivert design: no kernel driver, no checksum math, no NAT
table, less unsafe code, and it does not fight the OS. It meets the three
priorities — reliable, easy, fast (a local proxy adds microseconds).

## Optional: WinDivert for apps that cannot use a proxy

Only needed if a future app cannot be told to use a proxy. Then capture that
app's packets by PID with the WinDivert FLOW layer, rewrite the destination to
the local proxy (`127.0.0.1:PORT`), and let the proxy bind and forward. The
FLOW layer gives the PID; the NETWORK layer does not. This stays out of scope
until a non-proxy app actually needs it.

## Modules

| File | Role | State |
|------|------|-------|
| `src/config.rs` | TOML config + validation | tested |
| `src/interfaces.rs` | adapter name -> live ifindex/IP | matching tested; OS read verified code ready |
| `src/engine/flow.rs` | connection -> process table | tested; only for the optional path |
| `src/engine/nat.rs` | source NAT | **unused** — kept for reference only |
| `src/proxy.rs` (new) | per-interface HTTP CONNECT proxy + `IP_UNICAST_IF` bind | to write |
| `src/gui/mod.rs` | classic Win32 GUI (native-windows-gui) | skeleton ready |
| `src/main.rs` | start order + threads | update: start proxies, not WinDivert |

## The egress mechanism

`IP_UNICAST_IF` is the one supported way to force a socket's outgoing interface.
Set it before `connect`. `socket2` wraps it as `bind_device_by_index_v4`. Get
the index from `enumerate_adapters`.

## Reliability rules

1. **Adapter reconnect.** On a network-change event, re-resolve adapters and
   update each proxy's bound ifindex. Open connections keep their old binding;
   new ones use the new index.
2. **Fail closed.** If an interface is down, the proxy refuses the connection
   rather than letting it fall back to another interface. No silent leak.
3. **IPv6.** Bind the v4 interface with `IP_UNICAST_IF`; also handle or block
   IPv6 (`IPV6_UNICAST_IF`, host byte order), or an app may use IPv6 and bypass
   the rules.
4. **DNS.** The browser's DNS lookups must also leave on the right interface. An
   HTTP CONNECT proxy receives the hostname and resolves it itself, so bind the
   resolver's socket to the same interface, or DNS leaks to the default path.
5. **Elevation.** The proxy design needs no driver, so no Administrator rights
   are required. (The optional WinDivert path does need elevation.)

## Verified crate facts (checked 2026-11-15)

- `windivert` 0.6.0: `WinDivert::flow(filter, priority, flags)` sets sniff +
  recv-only itself; `recv(None)` on the flow layer; address accessors
  `process_id()`, `local_address()`, `remote_address()`, `local_port()`,
  `remote_port()`, `event()`; `WinDivertEvent::FlowEstablished` / `FlowDeleted`.
- `windows` 0.58: `GetAdaptersAddresses` loop; confirm the `Anonymous1.Anonymous.IfIndex`
  union path and the `sin_addr.S_un.S_addr` access on the build box.
- `socket2` 0.5: `bind_device_by_index_v4` sets `IP_UNICAST_IF`.

## Implemented (2026-11-15): `netroute/dist/netroute.exe`

The proxy-per-interface design above is now built, but in C# on the .NET
Framework that ships inside Windows rather than in Rust. Reason: the goal is a
file that launches with zero setup on any Windows PC. The Framework compiler
(`csc.exe`) is already on every Windows 10/11 machine, and the resulting 70 KB
exe needs no runtime install, no driver and no Administrator rights. The Rust
skeleton in `netroute/src` is kept for reference only.

What the exe does, mapped to the sections above:

- **Egress**: `Adapters.BoundSocket` sets `IP_UNICAST_IF` (option 31, index in
  network byte order) and binds to the adapter's own IPv4 address before
  `connect`. Verified: traffic through the proxy carries the Wi-Fi public IP;
  a connection on a down or missing adapter returns 503 and sends nothing.
- **DNS** (reliability rule 4): `InterfaceDns` sends A queries over a UDP
  socket bound the same way, to the adapter's own DNS servers, falling back to
  1.1.1.1 / 8.8.8.8 still via that adapter.
- **Adapter reconnect** (rule 1): the window re-reads adapters every 2 s and on
  `NetworkChange` events; each proxy looks the adapter up per request, so new
  connections follow a new ifindex while open tunnels keep theirs.
- **Fail closed** (rule 2): no fallback to another adapter, ever.
- **IPv6** (rule 3): outgoing side is IPv4 only; browsers reach the proxy over
  loopback so they cannot bypass it.
- **Controlled network**: a connection may name an upstream proxy; CONNECT and
  plain HTTP are chained through it on the bound socket. Verified against a test
  upstream on the LAN.
- **Apps**: browsers are launched with `--proxy-server` and a private
  `--user-data-dir` (Chromium family) or a profile with `user.js` (Firefox);
  other programs get `HTTP_PROXY`-style environment variables. The optional
  WinDivert path for proxy-blind apps remains unbuilt.
