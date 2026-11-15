// Per-adapter Wi-Fi control.
//
// Windows' own Wi-Fi panel acts on one adapter and shares saved networks
// between all of them, so picking the hotspot there tends to move the wrong
// adapter or join both adapters to the same network. Here every command names
// the adapter explicitly through `netsh wlan ... interface="<name>"`, which
// needs no Administrator rights.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetRoute
{
    public sealed class WifiNetwork
    {
        public string Ssid = "";
        public string Auth = "";        // e.g. WPA2-Personal, WPA3-Personal, Open
        public string Encryption = "";  // e.g. CCMP, TKIP, None
        public int Signal;              // best BSSID, percent
        public bool Saved;              // a profile exists on this adapter
    }

    public static class Wifi
    {
        /// adapter name -> SSID, for adapters that are connected right now.
        public static Dictionary<string, string> ConnectedSsids()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string name = null, state = null, ssid = null;
            foreach (var raw in Netsh("wlan show interfaces").Split('\n'))
            {
                string key, val;
                if (!Split(raw, out key, out val)) continue;
                if (key == "Name") { Flush(result, name, state, ssid); name = val; state = null; ssid = null; }
                else if (key == "State") state = val;
                else if (key == "SSID") ssid = val;
            }
            Flush(result, name, state, ssid);
            return result;
        }

        static void Flush(Dictionary<string, string> d, string name, string state, string ssid)
        {
            if (name == null) return;
            if (state != null && state.StartsWith("connected", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ssid))
                d[name] = ssid;
        }

        public static List<WifiNetwork> Scan(string adapter)
        {
            var list = new List<WifiNetwork>();
            var saved = new HashSet<string>(SavedProfiles(adapter), StringComparer.OrdinalIgnoreCase);
            WifiNetwork cur = null;
            foreach (var raw in Netsh("wlan show networks mode=bssid interface=" + Q(adapter)).Split('\n'))
            {
                string key, val;
                if (!Split(raw, out key, out val)) continue;
                if (key.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase))
                {
                    cur = new WifiNetwork { Ssid = val, Saved = saved.Contains(val) };
                    list.Add(cur);
                }
                else if (cur == null) continue;
                else if (key == "Authentication") cur.Auth = val;
                else if (key == "Encryption") cur.Encryption = val;
                else if (key == "Signal")
                {
                    int s;
                    if (int.TryParse(val.TrimEnd('%', ' '), out s) && s > cur.Signal) cur.Signal = s;
                }
            }
            // Strongest first; hidden (empty-name) networks last.
            list.Sort(delegate (WifiNetwork x, WifiNetwork y)
            {
                if ((x.Ssid.Length == 0) != (y.Ssid.Length == 0)) return x.Ssid.Length == 0 ? 1 : -1;
                return y.Signal.CompareTo(x.Signal);
            });
            return list;
        }

        public static List<string> SavedProfiles(string adapter)
        {
            var list = new List<string>();
            foreach (var raw in Netsh("wlan show profiles interface=" + Q(adapter)).Split('\n'))
            {
                string key, val;
                if (!Split(raw, out key, out val)) continue;
                if (key.EndsWith("User Profile", StringComparison.OrdinalIgnoreCase) && val.Length > 0) list.Add(val);
            }
            return list;
        }

        /// Join `ssid` on `adapter`. Returns null on success, else an error message.
        /// `password` may be empty for an open or already-saved network.
        /// `exclusive` sets every other saved network on this adapter to manual
        /// connect, so the adapter stops hopping back to them by itself.
        public static string Connect(string adapter, string ssid, string auth, string encryption, string password, bool exclusive)
        {
            if (string.IsNullOrEmpty(ssid)) return "Enter or choose a network name.";
            var saved = new HashSet<string>(SavedProfiles(adapter), StringComparer.OrdinalIgnoreCase);
            bool open = auth.StartsWith("Open", StringComparison.OrdinalIgnoreCase) || (auth.Length == 0 && password.Length == 0 && !saved.Contains(ssid));

            if (auth.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0 && password.Length > 0)
                return "'" + ssid + "' is an enterprise (802.1X) network. Join it once through Windows Settings on this adapter; after that it will connect from here.";

            if (password.Length > 0 || (!saved.Contains(ssid) && open))
            {
                // Write a profile for this adapter only.
                string xml = ProfileXml(ssid, open ? "open" : AuthToken(auth), open ? "none" : EncToken(encryption), open ? null : password);
                string tmp = Path.Combine(Path.GetTempPath(), "netroute-wlan-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(tmp, xml, new UTF8Encoding(false));
                    if (saved.Contains(ssid)) Netsh("wlan delete profile name=" + Q(ssid) + " interface=" + Q(adapter));
                    var outp = Netsh("wlan add profile filename=" + Q(tmp) + " interface=" + Q(adapter) + " user=all");
                    if (outp.IndexOf("is added", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // Fall back to a per-user profile if all-user is not allowed.
                        outp = Netsh("wlan add profile filename=" + Q(tmp) + " interface=" + Q(adapter) + " user=current");
                        if (outp.IndexOf("is added", StringComparison.OrdinalIgnoreCase) < 0)
                            return "Could not save the network on '" + adapter + "':\n" + outp.Trim();
                    }
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else if (!saved.Contains(ssid))
            {
                return "'" + ssid + "' needs a password the first time.";
            }

            if (exclusive)
            {
                foreach (var p in SavedProfiles(adapter))
                {
                    if (string.Equals(p, ssid, StringComparison.OrdinalIgnoreCase)) continue;
                    Netsh("wlan set profileparameter name=" + Q(p) + " interface=" + Q(adapter) + " connectionmode=manual");
                }
                Netsh("wlan set profileparameter name=" + Q(ssid) + " interface=" + Q(adapter) + " connectionmode=auto");
            }

            var res = Netsh("wlan connect name=" + Q(ssid) + " interface=" + Q(adapter));
            if (res.IndexOf("completed successfully", StringComparison.OrdinalIgnoreCase) < 0)
                return "Windows refused the connection on '" + adapter + "':\n" + res.Trim();
            return null;
        }

        /// Why Windows last failed to join `ssid` on `adapter`, from the WLAN
        /// AutoConfig event log (event 8002). Null when nothing recent is found.
        public static string LastFailureReason(string adapter, string ssid)
        {
            try
            {
                string guid = null;
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    if (string.Equals(nic.Name, adapter, StringComparison.OrdinalIgnoreCase)) { guid = nic.Id; break; }
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "Microsoft-Windows-WLAN-AutoConfig/Operational",
                    System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[(EventID=8002) and TimeCreated[timediff(@SystemTime) <= 600000]]]");
                q.ReverseDirection = true;
                using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        var ev = reader.ReadEvent();
                        if (ev == null) break;
                        string msg;
                        try { msg = ev.FormatDescription() ?? ""; } catch { continue; }
                        if (guid != null && msg.IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (msg.IndexOf(ssid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        int r = msg.IndexOf("Failure Reason:", StringComparison.OrdinalIgnoreCase);
                        if (r < 0) continue;
                        var line = msg.Substring(r + 15).Trim();
                        int nl = line.IndexOfAny(new[] { '\r', '\n' });
                        if (nl > 0) line = line.Substring(0, nl).Trim();
                        return line;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string Disconnect(string adapter)
        {
            var res = Netsh("wlan disconnect interface=" + Q(adapter));
            return res.IndexOf("completed successfully", StringComparison.OrdinalIgnoreCase) >= 0 ? null : res.Trim();
        }

        // -- helpers -----------------------------------------------------------

        static string AuthToken(string auth)
        {
            var a = auth.ToLowerInvariant();
            if (a.StartsWith("wpa3")) return "WPA3SAE";
            if (a.StartsWith("wpa2")) return "WPA2PSK";
            if (a.StartsWith("wpa")) return "WPAPSK";
            return "WPA2PSK"; // unknown or not scanned: the common case for hotspots
        }

        static string EncToken(string enc)
        {
            var e = enc.ToLowerInvariant();
            if (e.Contains("tkip")) return "TKIP";
            return "AES";
        }

        static string ProfileXml(string ssid, string auth, string enc, string key)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\n");
            sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\n");
            sb.Append("  <name>").Append(X(ssid)).Append("</name>\n");
            sb.Append("  <SSIDConfig><SSID><hex>").Append(Hex(ssid)).Append("</hex><name>").Append(X(ssid)).Append("</name></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>\n");
            sb.Append("  <connectionType>ESS</connectionType>\n");
            sb.Append("  <connectionMode>auto</connectionMode>\n");
            sb.Append("  <MSM><security>\n");
            sb.Append("    <authEncryption><authentication>").Append(auth).Append("</authentication><encryption>").Append(enc).Append("</encryption><useOneX>false</useOneX></authEncryption>\n");
            if (key != null)
                sb.Append("    <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>").Append(X(key)).Append("</keyMaterial></sharedKey>\n");
            sb.Append("  </security></MSM>\n");
            sb.Append("</WLANProfile>\n");
            return sb.ToString();
        }

        static string X(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string Hex(string s)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(s)) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        static string Q(string s) { return "\"" + s.Replace("\"", "") + "\""; }

        /// "   Key   : value" -> true. Values may be empty (hidden SSID).
        static bool Split(string line, out string key, out string val)
        {
            key = null; val = null;
            int i = line.IndexOf(" : ");
            if (i < 0) { if (line.TrimEnd().EndsWith(" :")) { key = line.Trim().TrimEnd(':').Trim(); val = ""; return key.Length > 0; } return false; }
            key = line.Substring(0, i).Trim();
            val = line.Substring(i + 3).Trim();
            return key.Length > 0;
        }

        public static string Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { }
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return outp + (err.Length > 0 ? "\n" + err : "");
                }
            }
            catch (Exception ex)
            {
                return "netsh failed: " + ex.Message;
            }
        }
    }

    // ======================================================================

    /// "Which network should this adapter join?"
    public sealed class WifiDialog : Form
    {
        readonly string adapter;
        readonly ListView list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        readonly TextBox ssidBox = new TextBox();
        readonly TextBox passBox = new TextBox { UseSystemPasswordChar = true };
        readonly CheckBox exclusive = new CheckBox { Text = "Only auto-join this network on this adapter (stops it drifting back to other saved networks)", AutoSize = true, Checked = true };
        readonly Button refresh = new Button { Text = "Rescan", AutoSize = true };
        readonly Button disconnect = new Button { Text = "Disconnect", AutoSize = true };
        readonly Button connect = new Button { Text = "Connect", AutoSize = true };
        readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        readonly Label current = new Label { AutoSize = true };
        readonly Label status = new Label { AutoSize = true, ForeColor = Color.FromArgb(110, 110, 110) };
        List<WifiNetwork> networks = new List<WifiNetwork>();

        public WifiDialog(string adapterName)
        {
            adapter = adapterName;
            Text = "Wi-Fi network for '" + adapter + "'";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(620), Ui.S(480));
            MinimumSize = new Size(Ui.S(520), Ui.S(400));
            CancelButton = close;
            AcceptButton = connect;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Ui.S(10)) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true, MaximumSize = new Size(Ui.S(590), 0), Margin = new Padding(0, 0, 0, Ui.S(6)),
                Text = "Pick the network this adapter should join. Only '" + adapter + "' changes; your other adapters stay where they are."
            };
            var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            top.Controls.Add(intro);
            top.Controls.Add(current);
            root.Controls.Add(top, 0, 0);

            list.Columns.Add("Network", Ui.S(260));
            list.Columns.Add("Security", Ui.S(130));
            list.Columns.Add("Signal", Ui.S(70));
            list.Columns.Add("Saved here", Ui.S(90));
            list.Dock = DockStyle.Fill;
            list.Margin = new Padding(0, Ui.S(4), 0, Ui.S(8));
            list.SelectedIndexChanged += delegate
            {
                if (list.SelectedItems.Count == 1)
                {
                    var n = list.SelectedItems[0].Tag as WifiNetwork;
                    if (n != null && n.Ssid.Length > 0) ssidBox.Text = n.Ssid;
                    passBox.Focus();
                }
            };
            list.DoubleClick += delegate { DoConnect(); };
            root.Controls.Add(list, 0, 1);

            var form = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0) };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.Controls.Add(new Label { Text = "Network name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, Ui.S(6), Ui.S(8), 0) }, 0, 0);
            ssidBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            ssidBox.Margin = new Padding(0, Ui.S(3), 0, Ui.S(3));
            form.Controls.Add(ssidBox, 1, 0);
            form.Controls.Add(new Label { Text = "Password:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, Ui.S(6), Ui.S(8), 0) }, 0, 1);
            passBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            passBox.Margin = new Padding(0, Ui.S(3), 0, Ui.S(3));
            form.Controls.Add(passBox, 1, 1);
            var hint = new Label { Text = "Leave the password empty for an open network or one marked 'saved here'. Type a name by hand for a hidden network.", AutoSize = true, ForeColor = Color.FromArgb(110, 110, 110), MaximumSize = new Size(Ui.S(520), 0), Margin = new Padding(0, 0, 0, Ui.S(4)) };
            form.Controls.Add(hint, 1, 2);
            form.Controls.Add(exclusive, 1, 3);
            root.Controls.Add(form, 0, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, Ui.S(8), 0, 0) };
            close.Margin = new Padding(Ui.S(6), 0, 0, 0);
            connect.Margin = new Padding(Ui.S(6), 0, 0, 0);
            disconnect.Margin = new Padding(Ui.S(6), 0, 0, 0);
            refresh.Margin = new Padding(Ui.S(6), 0, 0, 0);
            connect.Font = new Font(Font, FontStyle.Bold);
            buttons.Controls.Add(close);
            buttons.Controls.Add(connect);
            buttons.Controls.Add(disconnect);
            buttons.Controls.Add(refresh);
            root.Controls.Add(buttons, 0, 3);
            root.Controls.Add(status, 0, 4);

            Controls.Add(root);

            refresh.Click += delegate { DoScan(); };
            connect.Click += delegate { DoConnect(); };
            disconnect.Click += delegate { DoDisconnect(); };
            Shown += delegate { DoScan(); };
        }

        void Busy(bool busy, string text)
        {
            refresh.Enabled = connect.Enabled = disconnect.Enabled = list.Enabled = !busy;
            status.Text = text;
            UseWaitCursor = busy;
        }

        async void DoScan()
        {
            Busy(true, "Scanning with '" + adapter + "'…");
            try
            {
                var scanTask = Task.Run(delegate { return Wifi.Scan(adapter); });
                var curTask = Task.Run(delegate { return Wifi.ConnectedSsids(); });
                networks = await scanTask;
                var cur = await curTask;
                string ssid;
                cur.TryGetValue(adapter, out ssid);
                current.Text = ssid != null ? "Currently connected to: " + ssid : "Currently not connected to any network.";
                current.ForeColor = ssid != null ? Color.FromArgb(0, 128, 0) : Color.FromArgb(192, 0, 0);

                list.BeginUpdate();
                list.Items.Clear();
                foreach (var n in networks)
                {
                    var it = new ListViewItem(n.Ssid.Length > 0 ? n.Ssid : "(hidden network)");
                    it.SubItems.Add(n.Auth.Length > 0 ? n.Auth : "?");
                    it.SubItems.Add(n.Signal > 0 ? n.Signal + "%" : "");
                    it.SubItems.Add(n.Saved ? "saved" : "");
                    it.Tag = n;
                    if (ssid != null && string.Equals(n.Ssid, ssid, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(Font, FontStyle.Bold);
                    list.Items.Add(it);
                }
                list.EndUpdate();
                if (networks.Count == 0)
                    Busy(false, "No networks seen. If Wi-Fi scanning is blocked, turn on Location for desktop apps in Windows Settings > Privacy & security > Location, or type the name by hand.");
                else
                    Busy(false, networks.Count + " network(s) in range of '" + adapter + "'.");
            }
            catch (Exception ex) { Busy(false, "Scan failed: " + ex.Message); }
        }

        async void DoConnect()
        {
            string ssid = ssidBox.Text.Trim();
            string pass = passBox.Text;
            if (ssid.Length == 0) { status.Text = "Choose a network first."; return; }
            WifiNetwork n = null;
            foreach (var x in networks) if (string.Equals(x.Ssid, ssid, StringComparison.OrdinalIgnoreCase)) { n = x; break; }
            string auth = n != null ? n.Auth : "", enc = n != null ? n.Encryption : "";
            bool excl = exclusive.Checked;

            Busy(true, "Connecting '" + adapter + "' to '" + ssid + "'…");
            string err = await Task.Run(delegate { return Wifi.Connect(adapter, ssid, auth, enc, pass, excl); });
            if (err != null)
            {
                Busy(false, "");
                MessageBox.Show(this, err, "Could not connect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Wait for Windows to actually associate and get an address.
            string got = null;
            for (int i = 0; i < 25 && got == null; i++)
            {
                await Task.Delay(800);
                var cur = await Task.Run(delegate { return Wifi.ConnectedSsids(); });
                string s;
                if (cur.TryGetValue(adapter, out s) && string.Equals(s, ssid, StringComparison.OrdinalIgnoreCase)) got = s;
                status.Text = "Waiting for '" + adapter + "' to join '" + ssid + "'… (" + (i + 1) + ")";
            }
            passBox.Text = "";
            if (got != null)
            {
                Log.Write("'" + adapter + "' joined Wi-Fi network '" + ssid + "'.");
                Busy(false, "Connected.");
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                string reason = await Task.Run(delegate { return Wifi.LastFailureReason(adapter, ssid); });
                string why = reason != null ? "Windows says: " + reason + "." : "Windows accepted the request but the adapter never joined.";
                bool keyProblem = reason != null && (reason.IndexOf("PSK", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || reason.IndexOf("key exchange", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || reason.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0);
                Log.Write("'" + adapter + "' could not join '" + ssid + "'. " + why);
                if (keyProblem)
                {
                    Busy(false, why + " The saved password for this network is wrong or out of date.");
                    MessageBox.Show(this,
                        why + "\n\nThe password Windows has saved for '" + ssid + "' no longer matches. "
                        + "Type the network's current password in the Password box and press Connect again. "
                        + "netroute will replace the saved copy on '" + adapter + "' only.",
                        "Password does not match", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    passBox.Focus();
                }
                else
                {
                    Busy(false, why + " Try again, move closer, or re-enter the password.");
                    DoScan();
                }
            }
        }

        async void DoDisconnect()
        {
            Busy(true, "Disconnecting '" + adapter + "'…");
            string err = await Task.Run(delegate { return Wifi.Disconnect(adapter); });
            if (err != null) MessageBox.Show(this, err, "netroute", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Log.Write("'" + adapter + "' disconnected from Wi-Fi.");
            DoScan();
        }
    }
}
