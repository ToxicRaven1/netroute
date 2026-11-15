<p align="center">
  <img src="app/netroute-logo.png" width="96" alt="netroute logo">
</p>

<h1 align="center">netroute</h1>

<p align="center">
  Send each app out the internet connection <em>you</em> choose, on Windows.<br>
  One 110 KB exe. No install, no driver, no admin.
</p>

---

Got two Wi-Fi adapters, a phone hotspot, a VPN? netroute turns each one into a
**connection**, and lets you launch any browser or app so that all of its
traffic leaves on that connection and nowhere else. Run one browser on your
home Wi-Fi and a second copy of the same browser on your hotspot, side by side.

## Features

- **Pick the adapter per connection** from a dropdown of what is plugged in. Plug in a USB Wi-Fi adapter and it appears within seconds.
- **Pick the Wi-Fi network per adapter**, without Windows moving your other adapter or joining both to the same network.
- **Add connections** with one click. Three adapters, three connections.
- **Launch apps on a connection.** Browsers get a private profile per connection, so the same browser can run twice on two networks with separate logins.
- **Fail closed.** If an adapter drops, apps on it get an error instead of silently leaking onto another connection. DNS lookups go out the same adapter too.
- **Test button** shows the public IP each connection leaves on, so you can see it working.
- **Zero setup.** First run detects your adapters and browsers. Settings save themselves. Closing hides to the tray.

## Get it running

1. Download `netroute.exe` from the Releases page, or build it (below).
2. Double-click it.

That is all. It runs on any Windows 10 or 11 PC because it uses the .NET
Framework that is already part of Windows. Share the single exe with anyone.

## Using it

**Connections** (top half): one row per network path.

| Field | What it does |
|---|---|
| Name | Any label. Apps pick a connection by this name. |
| Adapter | Which Windows adapter this connection is bound to. |
| Status | `on · 192.168.x.x` when serving; `down`, `not plugged in` or `port busy` otherwise. |
| Port | Local port on 127.0.0.1 that apps on this connection use. Never reachable from outside. |
| Upstream proxy | Only for a network that itself needs a proxy to reach the internet, e.g. `proxy.example.edu:8080`. Leave empty otherwise. |
| Network… | For Wi-Fi adapters: scan, choose a network, enter the password once, and join with **this adapter only**. |
| Test | Fetch your public IP through this connection. |

**Apps** (bottom half): one row per program. **Add browser** lists installed
browsers; **Add app…** picks any exe; **Launch** starts it on its connection.
Anything you do not launch from netroute keeps using Windows' normal default.

Typical two-adapter setup: leave the built-in Wi-Fi on your main network, click
**Network…** on the second row and join your hotspot with the USB adapter,
then **Test** both rows and watch two different IPs come back.

## How it works

Each connection is a tiny HTTP proxy on `127.0.0.1:PORT`. For every request it
opens its outgoing socket bound to that connection's adapter (`IP_UNICAST_IF`
plus a bind to the adapter's own address), so Windows must send the packets on
that adapter regardless of the route table. DNS queries are sent the same way to
that adapter's own DNS servers. Browsers are launched with the proxy on their
command line (`--proxy-server` for the Chromium family, a profile with `user.js`
for Firefox); other programs get `HTTP_PROXY`-style environment variables.

No kernel driver, no packet capture, no changes to Windows' global proxy or
routing. See [architecture.md](architecture.md) for the design history.

## Limits

- Apps that ignore proxy settings and environment variables (some games and chat clients) cannot be forced onto a connection without a driver; they keep using the Windows default.
- Outgoing side is IPv4. Apps talk to the proxy over loopback, so they cannot bypass it over IPv6.
- Enterprise (802.1X) Wi-Fi networks must be joined once through Windows Settings; after that netroute can select them.

## Build from source

Windows ships the compiler. From the repo folder:

```
build.cmd
```

Output: `dist\netroute.exe`. Source lives in `app\`:

| File | Role |
|---|---|
| `Proxy.cs` | Per-connection HTTP proxy, adapter-bound sockets, fail-closed |
| `InterfaceDns.cs` | DNS lookups sent out the chosen adapter |
| `Adapters.cs` | Reads live adapters from Windows |
| `Wifi.cs` | Per-adapter Wi-Fi scanning and joining, plus the picker dialog |
| `Launcher.cs` | Starts browsers and apps with the right proxy |
| `MainForm.cs` | The window, tray icon, first-run setup |
| `Model.cs` | Settings, JSON persistence, logging |
| `make-icon.ps1` | Regenerates `netroute.ico` from the logo drawing |

Logs go to `%LOCALAPPDATA%\netroute\netroute.log`. Hidden diagnostics:
`netroute.exe --test 8081` and `netroute.exe --scan "Wi-Fi 2"` write their
result to `%LOCALAPPDATA%\netroute\debug.txt`.

The `src/` folder and `Cargo.toml` are an earlier Rust sketch of a
packet-capture design that was dropped; they are kept for reference only.
#
