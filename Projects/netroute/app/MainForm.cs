// The window.
//
//   Connections  — one row per network path: a name, the Windows adapter it
//                  uses (dropdown of what is plugged in right now), live status,
//                  the local proxy port, an optional upstream proxy, and an
//                  on/off switch. "Add connection" adds a row. Plug in another
//                  Wi-Fi adapter and it appears in the dropdown within seconds.
//   Apps         — one row per app: a name, the exe, the connection it uses,
//                  and a Launch button. "Add browser" lists installed browsers;
//                  "Add app" picks any exe.
//
// Everything saves as soon as it changes. Closing the window hides it to the
// tray so the proxies keep serving the browsers; Exit is in the tray menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetRoute
{
    public sealed class MainForm : Form
    {
        Settings settings;
        List<AdapterInfo> adapters = new List<AdapterInfo>();
        readonly Dictionary<string, ProxyServer> proxies = new Dictionary<string, ProxyServer>();
        readonly List<ConnectionRow> connRows = new List<ConnectionRow>();
        readonly List<AppRow> appRows = new List<AppRow>();

        Panel connPanel, appPanel;
        TextBox logBox;
        ToolStripStatusLabel statusLabel;
        NotifyIcon tray;
        Timer tick;
        bool exiting;
        bool trayHintShown;
        int tickCount;
        string adapterSignature = "";
        Dictionary<string, string> wifiSsids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly Color Green = Color.FromArgb(0, 128, 0);
        static readonly Color Red = Color.FromArgb(192, 0, 0);
        static readonly Color Orange = Color.FromArgb(200, 110, 0);
        static readonly Color Grey = Color.FromArgb(110, 110, 110);

        public MainForm()
        {
            Text = "netroute";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None; // pixel sizes go through Ui.S() instead
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.S(1100), Ui.S(640));
            MinimumSize = new Size(Ui.S(1000), Ui.S(520));
            Icon = MakeIcon();

            Log.Line += OnLogLine;
            BuildLayout();
            BuildTray();

            settings = SettingsStore.Load();
            adapters = Adapters.Read();
            if (settings == null) settings = SeedFirstRun();

            foreach (var c in settings.Connections) AddConnectionRow(c);
            foreach (var a in settings.Apps) AddAppRow(a);
            ApplyAdapters(adapters);
            EnsureProxies();

            tick = new Timer();
            tick.Interval = 2000;
            tick.Tick += delegate
            {
                RefreshAdaptersAsync();
                if (++tickCount % 3 == 0) RefreshWifiAsync();
            };
            RefreshWifiAsync();
            tick.Start();
            NetworkChange.NetworkAddressChanged += delegate { BeginInvoke(new Action(RefreshAdaptersAsync)); };
            NetworkChange.NetworkAvailabilityChanged += delegate { BeginInvoke(new Action(RefreshAdaptersAsync)); };

            Log.Write("netroute ready. Proxies listen on 127.0.0.1 only. Settings: " + SettingsStore.FilePath);
        }

        // ------------------------------------------------------------------ layout

        void BuildLayout()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(10, 8, 10, 4);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // connections header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // column titles
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));        // connection rows
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // apps header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // column titles
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));        // app rows
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(96))); // log

            // Connections header
            var addConn = new Button { Text = "Add connection", AutoSize = true, Padding = new Padding(6, 0, 6, 0) };
            addConn.Click += delegate { OnAddConnection(); };
            root.Controls.Add(Header("Connections",
                "Each connection is a local proxy bound to one adapter. Apps you link to it leave on that adapter and nowhere else.",
                addConn), 0, 0);

            root.Controls.Add(ConnectionRow.MakeHeader(), 0, 1);

            connPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window };
            root.Controls.Add(connPanel, 0, 2);

            // Apps header
            var addBrowser = new Button { Text = "Add browser  ▾", AutoSize = true, Padding = new Padding(6, 0, 6, 0) };
            addBrowser.Click += delegate { ShowBrowserMenu(addBrowser); };
            var addApp = new Button { Text = "Add app…", AutoSize = true, Padding = new Padding(6, 0, 6, 0) };
            addApp.Click += delegate { OnAddApp(); };
            root.Controls.Add(Header("Apps",
                "Launch an app from here and it uses the connection you picked. The same browser can be added twice, once per connection.",
                addBrowser, addApp), 0, 3);

            root.Controls.Add(AppRow.MakeHeader(), 0, 4);

            appPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window };
            root.Controls.Add(appPanel, 0, 5);

            logBox = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Control, Font = new Font("Consolas", 8.5f), Margin = new Padding(0, 6, 0, 0)
            };
            root.Controls.Add(logBox, 0, 6);

            var status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("starting…");
            status.Items.Add(statusLabel);

            Controls.Add(root);
            Controls.Add(status);
        }

        static Control Header(string title, string hint, params Button[] buttons)
        {
            var t = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 6, 0, 2) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            var tl = new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0) };
            var hl = new Label { Text = hint, AutoSize = true, ForeColor = Grey, Margin = new Padding(0, 0, 0, 2) };
            left.Controls.Add(tl);
            left.Controls.Add(hl);
            var right = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            foreach (var b in buttons) { b.Margin = new Padding(6, 0, 0, 0); right.Controls.Add(b); }
            t.Controls.Add(left, 0, 0);
            t.Controls.Add(right, 1, 0);
            return t;
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            try { tray.Icon = new Icon(Icon, new Size(16, 16)); } catch { tray.Icon = Icon; }
            tray.Text = "netroute";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show netroute", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (stops all connections)", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "netroute is still running",
                        "Your connections stay up. Double-click the tray icon to open the window, right-click it to exit.",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            exiting = true;
            foreach (var p in proxies.Values) p.Stop();
            tray.Visible = false;
            Close();
            Application.Exit();
        }

        /// The icon compiled into the exe (app\netroute.ico), so the window, the
        /// taskbar, the tray and any shortcut all show the same logo. Falls back
        /// to drawing it if the resource cannot be read.
        static Icon MakeIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return DrawIcon();
        }

        static Icon DrawIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b1 = new SolidBrush(Color.FromArgb(30, 110, 200)))
                using (var b2 = new SolidBrush(Color.FromArgb(40, 170, 90)))
                using (var pen = new Pen(Color.White, 3f))
                {
                    g.FillEllipse(b1, 1, 1, 18, 18);
                    g.FillEllipse(b2, 13, 13, 18, 18);
                    g.DrawLine(pen, 8, 10, 24, 22);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // -------------------------------------------------------------- first run

        Settings SeedFirstRun()
        {
            var s = new Settings();
            foreach (var a in adapters)
            {
                if (!a.LooksLikeInternet) continue;
                s.Connections.Add(new Connection { Name = a.Name, Adapter = a.Name, Port = s.NextFreePort() });
            }
            if (s.Connections.Count == 0)
            {
                var first = adapters.Count > 0 ? adapters[0] : null;
                s.Connections.Add(new Connection { Name = first != null ? first.Name : "Connection 1", Adapter = first != null ? first.Name : "", Port = 8081 });
            }
            int i = 0;
            foreach (var b in Launcher.DetectBrowsers())
            {
                b.ConnectionId = s.Connections[i % s.Connections.Count].Id;
                s.Apps.Add(b);
                i++;
            }
            SettingsStore.Save(s);
            Log.Write("First run: found " + s.Connections.Count + " internet connection(s) and " + s.Apps.Count + " browser(s).");
            return s;
        }

        // ------------------------------------------------------------ connections

        void OnAddConnection()
        {
            var c = new Connection { Name = "Connection " + (settings.Connections.Count + 1), Port = settings.NextFreePort() };
            // Prefer an adapter no other connection uses yet.
            foreach (var a in adapters)
            {
                if (!a.IsUsable) continue;
                bool taken = false;
                foreach (var other in settings.Connections) if (string.Equals(other.Adapter, a.Name, StringComparison.OrdinalIgnoreCase)) taken = true;
                if (!taken) { c.Adapter = a.Name; c.Name = a.Name; break; }
            }
            settings.Connections.Add(c);
            AddConnectionRow(c);
            RefreshAppConnectionLists();
            Save();
            EnsureProxies();
            UpdateStatuses();
            connPanel.ScrollControlIntoView(connRows[connRows.Count - 1]);
        }

        void AddConnectionRow(Connection c)
        {
            var row = new ConnectionRow(c);
            row.SetAdapters(adapters);
            row.Name_.TextChanged += delegate { c.Name = row.Name_.Text; RefreshAppConnectionLists(); Save(); };
            row.Adapter.SelectionChangeCommitted += delegate
            {
                c.Adapter = row.SelectedAdapterName();
                Log.Write("'" + c.Name + "' now uses adapter '" + c.Adapter + "'.");
                Save(); UpdateStatuses();
            };
            row.Port.ValueChanged += delegate { c.Port = (int)row.Port.Value; Save(); EnsureProxies(); UpdateStatuses(); };
            row.Upstream.TextChanged += delegate { c.UpstreamProxy = row.Upstream.Text; Save(); };
            row.On.CheckedChanged += delegate { c.Enabled = row.On.Checked; Save(); EnsureProxies(); UpdateStatuses(); };
            row.Test.Click += delegate { TestConnection(c, row); };
            row.Network.Click += delegate { OpenNetworkPicker(c); };
            row.Remove.Click += delegate { OnRemoveConnection(c, row); };
            connRows.Add(row);
            connPanel.Controls.Add(row);
            row.BringToFront(); // keep rows in the order they were added (Dock=Top stacks in reverse)
        }

        void OnRemoveConnection(Connection c, ConnectionRow row)
        {
            int linked = 0;
            foreach (var a in settings.Apps) if (a.ConnectionId == c.Id) linked++;
            if (linked > 0)
            {
                var r = MessageBox.Show(this, linked + " app(s) use '" + c.Name + "'. Remove the connection anyway? Those apps will need a new connection.",
                    "Remove connection", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }
            ProxyServer p;
            if (proxies.TryGetValue(c.Id, out p)) { p.Stop(); proxies.Remove(c.Id); }
            settings.Connections.Remove(c);
            foreach (var a in settings.Apps) if (a.ConnectionId == c.Id) a.ConnectionId = "";
            connRows.Remove(row);
            connPanel.Controls.Remove(row);
            row.Dispose();
            RefreshAppConnectionLists();
            Save();
            UpdateStatuses();
            Log.Write("Removed connection '" + c.Name + "'.");
        }

        /// Start, restart or stop proxies so they match the settings.
        void EnsureProxies()
        {
            foreach (var c in settings.Connections)
            {
                ProxyServer p;
                proxies.TryGetValue(c.Id, out p);
                if (!c.Enabled)
                {
                    if (p != null) { p.Stop(); proxies.Remove(c.Id); Log.Write("'" + c.Name + "' switched off."); }
                    continue;
                }
                if (p != null && p.IsRunning && p.Port == c.Port) continue;
                if (p != null) { p.Stop(); proxies.Remove(c.Id); }
                var conn = c;
                p = new ProxyServer(c.Port,
                    delegate { return Adapters.Find(adapters, conn.Adapter); },
                    delegate { return conn.UpstreamProxy; },
                    delegate { return conn.Name; });
                if (p.Start()) Log.Write("'" + c.Name + "' proxy listening on 127.0.0.1:" + c.Port + " → adapter '" + c.Adapter + "'.");
                else Log.Write("'" + c.Name + "' could not listen on port " + c.Port + ": " + p.LastError);
                proxies[c.Id] = p;
            }
        }

        void OpenNetworkPicker(Connection c)
        {
            var a = Adapters.Find(adapters, c.Adapter);
            if (a == null || !a.IsWireless)
            {
                MessageBox.Show(this, "'" + c.Adapter + "' is not a Wi-Fi adapter that is plugged in right now.", "netroute");
                return;
            }
            using (var dlg = new WifiDialog(a.Name))
            {
                dlg.ShowDialog(this);
            }
            RefreshWifiAsync();
            RefreshAdaptersAsync();
        }

        void RefreshWifiAsync()
        {
            Task.Run(delegate { return Wifi.ConnectedSsids(); }).ContinueWith(delegate (Task<Dictionary<string, string>> t)
            {
                if (t.Status != TaskStatus.RanToCompletion || IsDisposed) return;
                wifiSsids = t.Result;
                UpdateStatuses();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        async void TestConnection(Connection c, ConnectionRow row)
        {
            if (!c.Enabled) { MessageBox.Show(this, "Switch the connection on first.", "netroute"); return; }
            row.Test.Enabled = false;
            Log.Write("Testing '" + c.Name + "'…");
            try
            {
                string ip = await Task.Run(delegate
                {
                    var req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org/");
                    req.Proxy = new WebProxy("127.0.0.1", c.Port);
                    req.Timeout = 15000;
                    req.UserAgent = "netroute";
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream()))
                        return sr.ReadToEnd().Trim();
                });
                Log.Write("'" + c.Name + "' works. The internet sees this connection as " + ip + ".");
                MessageBox.Show(this, "'" + c.Name + "' works.\n\nThe internet sees it as " + ip + ".\nAdapter: " + c.Adapter,
                    "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (WebException wex)
            {
                string detail = wex.Message;
                try
                {
                    var r = wex.Response as HttpWebResponse;
                    if (r != null) using (var sr = new StreamReader(r.GetResponseStream())) detail = sr.ReadToEnd().Trim();
                }
                catch { }
                Log.Write("'" + c.Name + "' test failed: " + detail);
                MessageBox.Show(this, "'" + c.Name + "' did not reach the internet.\n\n" + detail, "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Log.Write("'" + c.Name + "' test failed: " + ex.Message);
                MessageBox.Show(this, "'" + c.Name + "' did not reach the internet.\n\n" + ex.Message, "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { row.Test.Enabled = true; }
        }

        // ------------------------------------------------------------------- apps

        void ShowBrowserMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            var found = Launcher.DetectBrowsers();
            foreach (var b in found)
            {
                var app = b;
                menu.Items.Add(app.Name, null, delegate { AddApp(new LinkedApp { Name = app.Name, Path = app.Path }); });
            }
            if (found.Count == 0) menu.Items.Add(new ToolStripMenuItem("No browser found in the usual places") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Other program…", null, delegate { OnAddApp(); });
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        void OnAddApp()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Pick a program to route";
                dlg.Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = Path.GetFileNameWithoutExtension(dlg.FileName);
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(dlg.FileName);
                    if (!string.IsNullOrEmpty(fvi.ProductName)) name = fvi.ProductName;
                }
                catch { }
                AddApp(new LinkedApp { Name = name, Path = dlg.FileName });
            }
        }

        void AddApp(LinkedApp a)
        {
            // Link it to the connection with the fewest apps so a second browser
            // naturally lands on the second connection.
            Connection best = null; int bestCount = int.MaxValue;
            foreach (var c in settings.Connections)
            {
                int n = 0;
                foreach (var o in settings.Apps) if (o.ConnectionId == c.Id) n++;
                if (n < bestCount) { bestCount = n; best = c; }
            }
            if (best != null) a.ConnectionId = best.Id;
            settings.Apps.Add(a);
            AddAppRow(a);
            Save();
            appPanel.ScrollControlIntoView(appRows[appRows.Count - 1]);
            if (Launcher.KindOf(a.Path) == Launcher.Kind.Other)
                Log.Write("Added '" + a.Name + "'. It is not a known browser: it gets HTTP_PROXY settings, which only some programs follow.");
            else
                Log.Write("Added '" + a.Name + "'.");
        }

        void AddAppRow(LinkedApp a)
        {
            var row = new AppRow(a);
            row.SetConnections(settings.Connections);
            row.Name_.TextChanged += delegate { a.Name = row.Name_.Text; Save(); };
            row.Path_.TextChanged += delegate { a.Path = row.Path_.Text; Save(); };
            row.Browse.Click += delegate
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";
                    try { dlg.InitialDirectory = Path.GetDirectoryName(a.Path); } catch { }
                    if (dlg.ShowDialog(this) == DialogResult.OK) row.Path_.Text = dlg.FileName;
                }
            };
            row.Conn.SelectionChangeCommitted += delegate { a.ConnectionId = row.SelectedConnectionId(); Save(); };
            row.Launch.Click += delegate { LaunchApp(a); };
            row.Remove.Click += delegate
            {
                settings.Apps.Remove(a);
                appRows.Remove(row);
                appPanel.Controls.Remove(row);
                row.Dispose();
                Save();
                Log.Write("Removed app '" + a.Name + "'.");
            };
            appRows.Add(row);
            appPanel.Controls.Add(row);
            row.BringToFront();
        }

        void RefreshAppConnectionLists()
        {
            foreach (var r in appRows) r.SetConnections(settings.Connections);
        }

        void LaunchApp(LinkedApp a)
        {
            var c = settings.FindConnection(a.ConnectionId);
            if (c == null)
            {
                MessageBox.Show(this, "Pick a connection for '" + a.Name + "' first.", "netroute", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!c.Enabled)
            {
                c.Enabled = true;
                foreach (var r in connRows) if (r.Conn == c) r.On.Checked = true;
                EnsureProxies();
            }
            var ad = Adapters.Find(adapters, c.Adapter);
            if (ad == null || !ad.IsUsable)
            {
                var r = MessageBox.Show(this,
                    "Adapter '" + c.Adapter + "' for connection '" + c.Name + "' is not connected right now.\n\n"
                    + "The app will open but every page will fail until the adapter is up (nothing leaks onto another connection). Open it anyway?",
                    "netroute", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            try
            {
                Log.Write("Launching '" + a.Name + "' via '" + c.Name + "': " + Launcher.Describe(a, c));
                Launcher.Launch(a, c);
            }
            catch (Exception ex)
            {
                Log.Write("Launch failed: " + ex.Message);
                MessageBox.Show(this, "Could not start '" + a.Name + "'.\n\n" + ex.Message, "netroute", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------- status

        void RefreshAdaptersAsync()
        {
            Task.Run(delegate { return Adapters.Read(); }).ContinueWith(delegate (Task<List<AdapterInfo>> t)
            {
                if (t.Status == TaskStatus.RanToCompletion) ApplyAdapters(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ApplyAdapters(List<AdapterInfo> list)
        {
            adapters = list;
            var sb = new System.Text.StringBuilder();
            foreach (var a in list) sb.Append(a.Name).Append('|').Append(a.Index).Append(';');
            var sig = sb.ToString();
            if (sig != adapterSignature)
            {
                if (adapterSignature.Length > 0) Log.Write("Adapters changed; dropdowns updated.");
                adapterSignature = sig;
                foreach (var r in connRows) r.SetAdapters(list);
            }
            UpdateStatuses();
        }

        void UpdateStatuses()
        {
            int on = 0, up = 0;
            foreach (var r in connRows)
            {
                var c = r.Conn;
                var a = Adapters.Find(adapters, c.Adapter);
                ProxyServer p;
                proxies.TryGetValue(c.Id, out p);
                string ssid = null;
                if (a != null && a.IsWireless) wifiSsids.TryGetValue(a.Name, out ssid);
                string text; Color color;
                if (string.IsNullOrEmpty(c.Adapter)) { text = "pick an adapter"; color = Orange; }
                else if (a == null) { text = "not plugged in"; color = Grey; }
                else if (!a.IsUsable)
                {
                    text = a.IsWireless && string.IsNullOrEmpty(ssid) ? "down · pick a network →" : "down (no network)";
                    color = Red;
                }
                else if (!c.Enabled) { text = "off · " + a.IPv4; color = Grey; }
                else if (p == null || !p.IsRunning) { text = "port " + c.Port + " busy"; color = Orange; }
                else
                {
                    on++; up++;
                    text = "● on · " + (ssid != null ? ssid + " · " : "") + a.IPv4 + (a.HasGateway ? "" : " · no gateway");
                    if (p.Active > 0) text += " · " + p.Active + " live";
                    color = Green;
                }
                if (c.Enabled && a != null && !a.IsUsable) on++;
                r.Status.Text = text;
                r.Status.ForeColor = color;
                r.Status.Tag = a;
                r.Network.Enabled = a != null && a.IsWireless;
                var tip = a == null ? "No adapter named '" + c.Adapter + "' right now." : a.Description + "\nifIndex " + a.Index + (ssid != null ? "\nWi-Fi network: " + ssid : "") + (p != null ? "\n" + p.Served + " requests served" : "");
                r.Tips.SetToolTip(r.Status, tip);
            }
            statusLabel.Text = up + " of " + connRows.Count + " connection(s) up · " + appRows.Count + " app(s) · proxies on 127.0.0.1 only · close hides to tray";
        }

        void Save()
        {
            SettingsStore.Save(settings);
        }

        void OnLogLine(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(OnLogLine), line); } catch { } return; }
            if (logBox.Lines.Length > 300) logBox.Clear();
            logBox.AppendText(line + Environment.NewLine);
        }
    }

    // ============================================================= row controls

    sealed class ConnectionRow : Panel
    {
        public readonly Connection Conn;
        public readonly TextBox Name_ = new TextBox();
        public readonly ComboBox Adapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        public readonly Label Status = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        public readonly NumericUpDown Port = new NumericUpDown { Minimum = 1025, Maximum = 65535 };
        public readonly TextBox Upstream = new TextBox();
        public readonly CheckBox On = new CheckBox { Text = "", AutoSize = true, Anchor = AnchorStyles.None };
        public readonly Button Network = new Button { Text = "Network…" };
        public readonly Button Test = new Button { Text = "Test" };
        public readonly Button Remove = new Button { Text = "✕", Font = new Font("Segoe UI Symbol", 9f) };
        public readonly ToolTip Tips = new ToolTip();

        public static readonly int[] Widths = { 150, 0, 250, 70, 170, 40, 90, 64, 36 };
        public static readonly string[] Titles = { "Name", "Adapter (what's plugged in)", "Status", "Port", "Upstream proxy (optional)", "On", "Wi-Fi network", "", "" };

        public ConnectionRow(Connection c)
        {
            Conn = c;
            Dock = DockStyle.Top;
            Height = Ui.S(34);
            Margin = new Padding(0);
            var t = Grid(Widths);
            Name_.Text = c.Name;
            Port.Value = Math.Max(Port.Minimum, Math.Min(Port.Maximum, c.Port));
            Upstream.Text = c.UpstreamProxy;
            On.Checked = c.Enabled;
            Tips.SetToolTip(Upstream, "Only if this network itself needs a proxy to reach the internet, e.g. proxy.example.edu:8080 or user:pass@host:port. Leave empty for a hotspot, home Wi-Fi or VPN.");
            Tips.SetToolTip(Port, "Apps on this connection talk to 127.0.0.1 on this port.");
            Tips.SetToolTip(Test, "Fetch your public IP through this connection to prove which network it leaves on.");
            Tips.SetToolTip(Network, "Choose which Wi-Fi network this adapter joins, without touching your other adapters.");
            Tips.SetToolTip(Remove, "Remove this connection");
            Tips.SetToolTip(On, "Switch the proxy for this connection on or off");
            Fill(t, Name_, Adapter, Status, Port, Upstream, On, Network, Test, Remove);
            Controls.Add(t);
        }

        public void SetAdapters(List<AdapterInfo> adapters)
        {
            if (Adapter.DroppedDown) return;
            Adapter.BeginUpdate();
            Adapter.Items.Clear();
            bool found = false;
            foreach (var a in adapters)
            {
                string label = a.Name + (a.Description != null && a.Description != a.Name ? "  —  " + a.Description : "");
                if (!a.IsUsable) label += "  (down)";
                Adapter.Items.Add(new Item(a.Name, label));
                if (string.Equals(a.Name, Conn.Adapter, StringComparison.OrdinalIgnoreCase)) found = true;
            }
            if (!found && !string.IsNullOrEmpty(Conn.Adapter))
                Adapter.Items.Add(new Item(Conn.Adapter, Conn.Adapter + "  (not plugged in)"));
            foreach (Item it in Adapter.Items)
                if (string.Equals(it.Key, Conn.Adapter, StringComparison.OrdinalIgnoreCase)) { Adapter.SelectedItem = it; break; }
            Adapter.EndUpdate();
        }

        public string SelectedAdapterName()
        {
            var it = Adapter.SelectedItem as Item;
            return it == null ? "" : it.Key;
        }

        public static Control MakeHeader() { return HeaderRow(Widths, Titles); }

        // ---- shared helpers for both row types
        internal static TableLayoutPanel Grid(int[] widths)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = widths.Length, RowCount = 1, Margin = new Padding(0), Padding = new Padding(2, 0, 2, 0) };
            foreach (var w in widths)
                t.ColumnStyles.Add(w == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, Ui.S(w)));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return t;
        }

        internal static void Fill(TableLayoutPanel t, params Control[] controls)
        {
            for (int i = 0; i < controls.Length; i++)
            {
                var c = controls[i];
                if (c is CheckBox) { c.Anchor = AnchorStyles.None; }
                else if (c is Button) { c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 4, 3, 4); }
                else { c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 5, 3, 5); c.Anchor = AnchorStyles.Left | AnchorStyles.Right; }
                if (c is TextBox || c is ComboBox || c is NumericUpDown) { c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; c.Margin = new Padding(3, 6, 3, 0); }
                if (c is Label) { c.Dock = DockStyle.Fill; }
                t.Controls.Add(c, i, 0);
            }
        }

        internal static Control HeaderRow(int[] widths, string[] titles)
        {
            var t = Grid(widths);
            t.Dock = DockStyle.Top;
            t.Height = Ui.S(20);
            t.AutoSize = false;
            t.Padding = new Padding(3, 0, 3, 0);
            for (int i = 0; i < titles.Length; i++)
            {
                var l = new Label { Text = titles[i], AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, ForeColor = Color.FromArgb(110, 110, 110), Margin = new Padding(3, 0, 3, 0) };
                t.Controls.Add(l, i, 0);
            }
            return t;
        }

        internal sealed class Item
        {
            public readonly string Key; readonly string text;
            public Item(string key, string text) { Key = key; this.text = text; }
            public override string ToString() { return text; }
        }
    }

    sealed class AppRow : Panel
    {
        public readonly LinkedApp App;
        public readonly TextBox Name_ = new TextBox();
        public readonly TextBox Path_ = new TextBox();
        public readonly Button Browse = new Button { Text = "Browse…" };
        public readonly ComboBox Conn = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        public readonly Button Launch = new Button { Text = "Launch" };
        public readonly Button Remove = new Button { Text = "✕", Font = new Font("Segoe UI Symbol", 9f) };
        readonly ToolTip tips = new ToolTip();

        public static readonly int[] Widths = { 150, 0, 80, 200, 90, 36 };
        public static readonly string[] Titles = { "App", "Program", "", "Uses connection", "", "" };

        public AppRow(LinkedApp a)
        {
            App = a;
            Dock = DockStyle.Top;
            Height = Ui.S(34);
            Margin = new Padding(0);
            var t = ConnectionRow.Grid(Widths);
            Name_.Text = a.Name;
            Path_.Text = a.Path;
            tips.SetToolTip(Browse, "Choose a different program");
            tips.SetToolTip(Launch, "Open this app so that all of its traffic uses the chosen connection");
            tips.SetToolTip(Remove, "Remove this app");
            ConnectionRow.Fill(t, Name_, Path_, Browse, Conn, Launch, Remove);
            Controls.Add(t);
        }

        public void SetConnections(List<Connection> conns)
        {
            if (Conn.DroppedDown) return;
            Conn.BeginUpdate();
            Conn.Items.Clear();
            foreach (var c in conns) Conn.Items.Add(new ConnectionRow.Item(c.Id, c.Name.Length > 0 ? c.Name : "(unnamed)"));
            Conn.SelectedItem = null;
            foreach (ConnectionRow.Item it in Conn.Items) if (it.Key == App.ConnectionId) { Conn.SelectedItem = it; break; }
            Conn.EndUpdate();
        }

        public string SelectedConnectionId()
        {
            var it = Conn.SelectedItem as ConnectionRow.Item;
            return it == null ? "" : it.Key;
        }

        public static Control MakeHeader() { return ConnectionRow.HeaderRow(Widths, Titles); }
    }
}
