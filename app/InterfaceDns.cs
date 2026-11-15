// DNS lookups that leave on the chosen adapter.
//
// The system resolver would send the lookup out the default adapter, which is a
// leak and can also just fail (a filtered network may refuse the name). So the
// proxy asks the adapter's own DNS servers directly over a UDP socket bound to
// that adapter, with public resolvers as a fallback that still goes out the
// same adapter. Only A records are asked for: the upstream connection is IPv4.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NetRoute
{
    public static class InterfaceDns
    {
        sealed class Entry { public IPAddress Address; public DateTime Expires; }
        sealed class Answer { public IPAddress Address; public int Ttl; }

        static readonly object gate = new object();
        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly Random rng = new Random();

        static readonly IPAddress[] Fallback = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };

        public static async Task<IPAddress> Resolve(string host, AdapterInfo adapter)
        {
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork) return literal;
                throw new Exception("IPv6 destinations are not routed: " + host);
            }

            string key = adapter.Index + "|" + host;
            lock (gate)
            {
                Entry e;
                if (cache.TryGetValue(key, out e) && e.Expires > DateTime.UtcNow) return e.Address;
            }

            var servers = new List<IPAddress>();
            foreach (var s in adapter.DnsServers) if (!servers.Contains(s)) servers.Add(s);
            foreach (var s in Fallback) if (!servers.Contains(s)) servers.Add(s);

            Exception last = null;
            foreach (var server in servers)
            {
                try
                {
                    var ans = await Task.Run(delegate { return QueryBlocking(host, server, adapter); });
                    if (ans != null)
                    {
                        int ttl = ans.Ttl;
                        if (ttl < 10) ttl = 10;
                        if (ttl > 300) ttl = 300;
                        lock (gate)
                        {
                            cache[key] = new Entry { Address = ans.Address, Expires = DateTime.UtcNow.AddSeconds(ttl) };
                        }
                        return ans.Address;
                    }
                }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception("DNS lookup of " + host + " failed on " + adapter.Name
                + (last != null ? " (" + last.Message + ")" : ""));
        }

        static Answer QueryBlocking(string host, IPAddress server, AdapterInfo adapter)
        {
            ushort id = (ushort)rng.Next(1, 65535);
            var q = BuildQuery(host, id);

            using (var udp = Adapters.BoundSocket(adapter, SocketType.Dgram, ProtocolType.Udp))
            {
                udp.ReceiveTimeout = 3000;
                udp.SendTo(q, new IPEndPoint(server, 53));
                var buf = new byte[4096];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = udp.ReceiveFrom(buf, ref from);
                return ParseAnswer(buf, n, id);
            }
        }

        static byte[] BuildQuery(string host, ushort id)
        {
            var labels = host.TrimEnd('.').Split('.');
            var out_ = new List<byte>(64);
            out_.Add((byte)(id >> 8)); out_.Add((byte)id);
            out_.Add(0x01); out_.Add(0x00);   // flags: recursion desired
            out_.Add(0); out_.Add(1);         // QDCOUNT
            out_.Add(0); out_.Add(0);         // ANCOUNT
            out_.Add(0); out_.Add(0);         // NSCOUNT
            out_.Add(0); out_.Add(0);         // ARCOUNT
            foreach (var label in labels)
            {
                var b = Encoding.ASCII.GetBytes(label);
                if (b.Length == 0 || b.Length > 63) throw new Exception("bad host name: " + host);
                out_.Add((byte)b.Length);
                out_.AddRange(b);
            }
            out_.Add(0);
            out_.Add(0); out_.Add(1);         // QTYPE A
            out_.Add(0); out_.Add(1);         // QCLASS IN
            return out_.ToArray();
        }

        static Answer ParseAnswer(byte[] b, int n, ushort id)
        {
            if (n < 12) return null;
            if ((ushort)((b[0] << 8) | b[1]) != id) return null;
            int rcode = b[3] & 0x0F;
            if (rcode != 0) return null;
            int qd = (b[4] << 8) | b[5];
            int an = (b[6] << 8) | b[7];
            int pos = 12;
            for (int i = 0; i < qd; i++) { pos = SkipName(b, pos, n); pos += 4; }
            for (int i = 0; i < an && pos < n; i++)
            {
                pos = SkipName(b, pos, n);
                if (pos + 10 > n) return null;
                int type = (b[pos] << 8) | b[pos + 1];
                int rttl = (b[pos + 4] << 24) | (b[pos + 5] << 16) | (b[pos + 6] << 8) | b[pos + 7];
                int rdlen = (b[pos + 8] << 8) | b[pos + 9];
                pos += 10;
                if (type == 1 && rdlen == 4 && pos + 4 <= n)
                {
                    return new Answer
                    {
                        Address = new IPAddress(new[] { b[pos], b[pos + 1], b[pos + 2], b[pos + 3] }),
                        Ttl = rttl
                    };
                }
                pos += rdlen; // CNAME or other record: the A record usually follows
            }
            return null;
        }

        static int SkipName(byte[] b, int pos, int n)
        {
            while (pos < n)
            {
                int len = b[pos];
                if (len == 0) return pos + 1;
                if ((len & 0xC0) == 0xC0) return pos + 2;
                pos += len + 1;
            }
            return pos;
        }
    }
}
