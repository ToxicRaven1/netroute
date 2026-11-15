using System;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Without this marker Windows runs the exe with .NET 4.0 compatibility quirks
// (for example TLS 1.0 only), even though 4.8 is installed.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("netroute")]
[assembly: System.Reflection.AssemblyProduct("netroute")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace NetRoute
{
    /// Pixel sizes in the window are designed at 96 DPI and scaled here.
    public static class Ui
    {
        public static float Factor = 1f;
        public static int S(int px) { return (int)Math.Round(px * Factor); }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Factor = g.DpiX / 96f; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The Test button's own HTTPS client needs modern TLS.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } // Tls12 | Tls13
            catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }

            // Hidden diagnostics: "netroute.exe --test 8081" or "--scan "Wi-Fi 2"" write
            // their result to %LOCALAPPDATA%\netroute\debug.txt and exit.
            var argv = Environment.GetCommandLineArgs();
            if (argv.Length >= 3 && (argv[1] == "--test" || argv[1] == "--scan"))
            {
                string result;
                try
                {
                    if (argv[1] == "--test")
                    {
                        var req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org/");
                        req.Proxy = new WebProxy("127.0.0.1", int.Parse(argv[2]));
                        req.Timeout = 15000;
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
                            result = "OK " + sr.ReadToEnd().Trim();
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var kv in Wifi.ConnectedSsids()) sb.Append("connected: ").Append(kv.Key).Append(" -> ").Append(kv.Value).Append("\n");
                        foreach (var n in Wifi.Scan(argv[2]))
                        {
                            sb.Append("network: ").Append(n.Ssid).Append(" | ").Append(n.Auth).Append(" | ").Append(n.Encryption).Append(" | ").Append(n.Signal).Append("% | saved=").Append(n.Saved);
                            if (n.Saved) { var why = Wifi.LastFailureReason(argv[2], n.Ssid); if (why != null) sb.Append(" | last failure: ").Append(why); }
                            sb.Append("\n");
                        }
                        result = sb.ToString();
                    }
                }
                catch (Exception ex) { result = "FAIL " + ex.GetType().Name + ": " + ex.Message; }
                System.IO.Directory.CreateDirectory(SettingsStore.Dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(SettingsStore.Dir, "debug.txt"), result);
                return;
            }

            // One copy at a time: two copies would fight over the same ports.
            bool created;
            using (var mutex = new Mutex(true, "Local\\netroute-single-instance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("netroute is already running. Look for its icon in the tray next to the clock.",
                        "netroute", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm());
            }
        }
    }
}
