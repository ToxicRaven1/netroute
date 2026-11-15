// One local HTTP proxy per connection.
//
// A browser or app talks to 127.0.0.1:PORT. For every request the proxy opens
// its upstream socket bound to the connection's adapter (IP_UNICAST_IF plus a
// bind to the adapter's own address), so the traffic leaves on that adapter
// whatever the route table says. If the adapter is down the proxy refuses the
// request. It never falls back to another adapter: fail closed, no silent leak.
//
// Supported: HTTPS via CONNECT (what browsers use for nearly everything), plain
// HTTP via absolute URLs, and an optional upstream proxy for a network that
// only reaches the internet through its own proxy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRoute
{
    public sealed class ProxyServer
    {
        readonly Func<AdapterInfo> getAdapter;
        readonly Func<string> getUpstream;
        readonly Func<string> getName;
        TcpListener listener;
        volatile bool running;
        int active;
        long served;

        public int Port { get; private set; }
        public bool IsRunning { get { return running; } }
        public int Active { get { return active; } }
        public long Served { get { return Interlocked.Read(ref served); } }
        public string LastError { get; private set; }

        public ProxyServer(int port, Func<AdapterInfo> getAdapter, Func<string> getUpstream, Func<string> getName)
        {
            Port = port;
            this.getAdapter = getAdapter;
            this.getUpstream = getUpstream;
            this.getName = getName;
        }

        /// Start listening. Returns false (and sets LastError) when the port is taken.
        public bool Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.ExclusiveAddressUse = true;
                Adapters.NoInherit(listener.Server);
                listener.Start(128);
                running = true;
                LastError = null;
                AcceptLoop();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                running = false;
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        async void AcceptLoop()
        {
            Log.Debug("accept loop started on port " + Port + " (thread " + Thread.CurrentThread.ManagedThreadId + ")");
            try
            {
                while (running)
                {
                    TcpClient client = null;
                    Exception err = null;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception ex) { err = ex; }
                    if (err != null)
                    {
                        if (!running) return;
                        Log.Debug("accept error on port " + Port + ": " + err.Message);
                    }
                    if (client == null) { await Task.Delay(50); continue; }
                    Adapters.NoInherit(client.Client);
                    Handle(client);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Proxy on port " + Port + " stopped unexpectedly: " + ex.Message);
            }
            finally
            {
                Log.Debug("accept loop ended on port " + Port);
            }
        }

        async void Handle(TcpClient client)
        {
            Interlocked.Increment(ref active);
            Socket upstreamSock = null;
            Exception failure = null;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();
                var hdr = await ReadHeaders(cs);
                if (hdr == null) return;
                byte[] headerBytes = hdr[0], leftover = hdr[1];
                var head = Encoding.ASCII.GetString(headerBytes);
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                if (parts.Length < 3) { await Reply(cs, "400 Bad Request", "Malformed request line."); return; }
                string method = parts[0], target = parts[1];
                Log.Debug("port " + Port + ": " + method + " " + target);

                var adapter = getAdapter();
                if (adapter == null || !adapter.IsUsable)
                {
                    // Fail closed. The browser shows an error instead of leaking.
                    await Reply(cs, "503 Service Unavailable",
                        "netroute: connection '" + getName() + "' is down (adapter not connected). Nothing was sent.");
                    return;
                }

                var upstream = Upstream.Parse(getUpstream());
                Interlocked.Increment(ref served);

                if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    string host; int port;
                    SplitHostPort(target, 443, out host, out port);

                    if (upstream != null)
                    {
                        upstreamSock = await ConnectVia(adapter, upstream);
                        var us = new NetworkStream(upstreamSock, true);
                        var req = "CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n"
                                + upstream.AuthHeader() + "Proxy-Connection: keep-alive\r\n\r\n";
                        var rb = Encoding.ASCII.GetBytes(req);
                        await us.WriteAsync(rb, 0, rb.Length);
                        var uHdr = await ReadHeaders(us);
                        if (uHdr == null) { await Reply(cs, "502 Bad Gateway", "Upstream proxy closed the connection."); return; }
                        byte[] uHead = uHdr[0], uLeft = uHdr[1];
                        var uLine = Encoding.ASCII.GetString(uHead).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                        if (!uLine.Contains(" 200"))
                        {
                            // Pass the upstream's refusal straight through so the browser sees why.
                            await cs.WriteAsync(uHead, 0, uHead.Length);
                            return;
                        }
                        await WriteAscii(cs, "HTTP/1.1 200 Connection Established\r\n\r\n");
                        if (uLeft.Length > 0) await cs.WriteAsync(uLeft, 0, uLeft.Length);
                        if (leftover.Length > 0) await us.WriteAsync(leftover, 0, leftover.Length);
                        await Relay(cs, us, client.Client, upstreamSock);
                    }
                    else
                    {
                        var ip = await InterfaceDns.Resolve(host, adapter);
                        upstreamSock = await ConnectBound(adapter, ip, port);
                        var us = new NetworkStream(upstreamSock, true);
                        await WriteAscii(cs, "HTTP/1.1 200 Connection Established\r\n\r\n");
                        if (leftover.Length > 0) await us.WriteAsync(leftover, 0, leftover.Length);
                        await Relay(cs, us, client.Client, upstreamSock);
                    }
                    return;
                }

                // Plain HTTP. Browsers send the full URL to a proxy.
                Uri uri;
                if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || uri.Scheme != "http")
                {
                    await Reply(cs, "400 Bad Request", "netroute only proxies http:// URLs and CONNECT tunnels.");
                    return;
                }

                if (upstream != null)
                {
                    upstreamSock = await ConnectVia(adapter, upstream);
                    var us = new NetworkStream(upstreamSock, true);
                    var fwd = RewriteHeaders(lines, lines[0], upstream.AuthHeader(), false);
                    await us.WriteAsync(fwd, 0, fwd.Length);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, 0, leftover.Length);
                    await Relay(cs, us, client.Client, upstreamSock);
                }
                else
                {
                    var ip = await InterfaceDns.Resolve(uri.Host, adapter);
                    upstreamSock = await ConnectBound(adapter, ip, uri.Port);
                    var us = new NetworkStream(upstreamSock, true);
                    var reqLine = method + " " + uri.PathAndQuery + " " + parts[2];
                    var fwd = RewriteHeaders(lines, reqLine, "", true);
                    await us.WriteAsync(fwd, 0, fwd.Length);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, 0, leftover.Length);
                    await Relay(cs, us, client.Client, upstreamSock);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            if (failure != null)
            {
                LastError = failure.Message;
                Log.Debug("port " + Port + ": request failed: " + failure.GetType().Name + ": " + failure.Message);
                try
                {
                    if (client.Connected)
                        await Reply(client.GetStream(), "502 Bad Gateway", "netroute (" + getName() + "): " + failure.Message);
                }
                catch { }
            }
            Interlocked.Decrement(ref active);
            try { if (upstreamSock != null) upstreamSock.Close(); } catch { }
            try { client.Close(); } catch { }
        }

        // -- helpers -----------------------------------------------------------

        static async Task<Socket> ConnectBound(AdapterInfo a, IPAddress ip, int port)
        {
            var s = Adapters.BoundSocket(a, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            try
            {
                var connect = Task.Factory.FromAsync(s.BeginConnect, s.EndConnect, new IPEndPoint(ip, port), null);
                var first = await Task.WhenAny(connect, Task.Delay(15000));
                if (first != connect) throw new TimeoutException("connect to " + ip + ":" + port + " timed out on " + a.Name);
                await connect;
                return s;
            }
            catch
            {
                s.Close();
                throw;
            }
        }

        static async Task<Socket> ConnectVia(AdapterInfo a, Upstream up)
        {
            var ip = await InterfaceDns.Resolve(up.Host, a);
            return await ConnectBound(a, ip, up.Port);
        }

        /// Read bytes until the blank line that ends the headers.
        /// Returns { headerBytes, leftoverBodyBytes }, or null on EOF.
        static async Task<byte[][]> ReadHeaders(Stream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) return null;
                ms.Write(buf, 0, n);
                var arr = ms.GetBuffer();
                int len = (int)ms.Length;
                int end = IndexOfBlankLine(arr, len);
                if (end >= 0)
                {
                    var head = new byte[end];
                    Array.Copy(arr, 0, head, 0, end);
                    var left = new byte[len - end];
                    Array.Copy(arr, end, left, 0, len - end);
                    return new[] { head, left };
                }
                if (len > 64 * 1024) throw new Exception("request headers too large");
            }
        }

        static int IndexOfBlankLine(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i + 1;
            }
            return -1;
        }

        /// Rebuild the header block. Hop-by-hop proxy headers are dropped; the
        /// connection is closed after one request so no stale keep-alive can
        /// cross to a different host.
        static byte[] RewriteHeaders(string[] lines, string requestLine, string extraHeader, bool stripProxyAuth)
        {
            var sb = new StringBuilder();
            sb.Append(requestLine).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.Length == 0) continue;
                var lower = l.ToLowerInvariant();
                if (lower.StartsWith("proxy-connection:")) continue;
                if (lower.StartsWith("connection:")) continue;
                if (stripProxyAuth && lower.StartsWith("proxy-authorization:")) continue;
                sb.Append(l).Append("\r\n");
            }
            sb.Append(extraHeader);
            sb.Append("Connection: close\r\n\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        static void SplitHostPort(string target, int defaultPort, out string host, out int port)
        {
            host = target; port = defaultPort;
            int i = target.LastIndexOf(':');
            if (i > 0 && target.IndexOf(']') < i)
            {
                host = target.Substring(0, i);
                int p;
                if (int.TryParse(target.Substring(i + 1), out p)) port = p;
            }
            host = host.Trim('[', ']');
        }

        static Task WriteAscii(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            return s.WriteAsync(b, 0, b.Length);
        }

        static Task Reply(Stream s, string status, string body)
        {
            var bb = Encoding.UTF8.GetBytes(body + "\n");
            var head = "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: "
                     + bb.Length + "\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            var all = new byte[hb.Length + bb.Length];
            Array.Copy(hb, all, hb.Length);
            Array.Copy(bb, 0, all, hb.Length, bb.Length);
            return s.WriteAsync(all, 0, all.Length);
        }

        /// Copy bytes both ways until either side closes, then close both.
        static async Task Relay(Stream a, Stream b, Socket sa, Socket sb)
        {
            var t1 = Pump(a, b, sb);
            var t2 = Pump(b, a, sa);
            await Task.WhenAny(t1, t2);
            // Give the other direction a moment to drain, then tear down.
            await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(2000));
            try { sa.Shutdown(SocketShutdown.Both); } catch { }
            try { sb.Shutdown(SocketShutdown.Both); } catch { }
        }

        static async Task Pump(Stream from, Stream to, Socket toSock)
        {
            var buf = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await to.WriteAsync(buf, 0, n);
                }
            }
            catch { }
            // Half-close: tell the other side we have nothing more to send, so it
            // can finish its response and close in its own time.
            try { toSock.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    /// "host:port", "http://host:port", or "user:pass@host:port".
    public sealed class Upstream
    {
        public string Host;
        public int Port = 8080;
        public string User;
        public string Pass;

        public static Upstream Parse(string text)
        {
            if (text == null) return null;
            var t = text.Trim();
            if (t.Length == 0) return null;
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7);
            if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) t = t.Substring(8);
            t = t.TrimEnd('/');
            var u = new Upstream();
            int at = t.LastIndexOf('@');
            if (at >= 0)
            {
                var cred = t.Substring(0, at);
                t = t.Substring(at + 1);
                int c = cred.IndexOf(':');
                if (c >= 0) { u.User = cred.Substring(0, c); u.Pass = cred.Substring(c + 1); }
                else u.User = cred;
            }
            int colon = t.LastIndexOf(':');
            if (colon > 0)
            {
                u.Host = t.Substring(0, colon);
                int p;
                if (int.TryParse(t.Substring(colon + 1), out p)) u.Port = p;
            }
            else u.Host = t;
            if (string.IsNullOrEmpty(u.Host)) return null;
            return u;
        }

        public string AuthHeader()
        {
            if (string.IsNullOrEmpty(User)) return "";
            var raw = Encoding.UTF8.GetBytes(User + ":" + (Pass ?? ""));
            return "Proxy-Authorization: Basic " + Convert.ToBase64String(raw) + "\r\n";
        }
    }
}
