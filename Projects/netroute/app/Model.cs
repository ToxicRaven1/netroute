// Settings model and persistence.
//
// Everything the user sets up in the window lives here and is saved as JSON in
// %LOCALAPPDATA%\netroute\settings.json. On the very first run the file does
// not exist, so MainForm seeds it from the adapters and browsers it finds.

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace NetRoute
{
    /// One network path: a Windows adapter plus the local proxy that is bound to it.
    public class Connection
    {
        public string Id { get; set; }
        /// The label shown in the window and in app dropdowns.
        public string Name { get; set; }
        /// The Windows friendly name of the adapter, for example "Wi-Fi" or "Wi-Fi 2".
        public string Adapter { get; set; }
        /// The local port the proxy listens on at 127.0.0.1.
        public int Port { get; set; }
        /// Optional upstream proxy this connection must go through, e.g. "proxy.school.edu:8080".
        /// Leave empty for a direct connection (home Wi-Fi, hotspot, VPN).
        public string UpstreamProxy { get; set; }
        /// Whether the proxy for this connection is running.
        public bool Enabled { get; set; }

        public Connection()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            Adapter = "";
            UpstreamProxy = "";
            Enabled = true;
        }
    }

    /// One app the user wants to send out a specific connection.
    public class LinkedApp
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// Full path to the exe.
        public string Path { get; set; }
        /// Extra command line arguments the user wants.
        public string Args { get; set; }
        /// The Connection.Id this app uses.
        public string ConnectionId { get; set; }

        public LinkedApp()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            Path = "";
            Args = "";
            ConnectionId = "";
        }
    }

    public class Settings
    {
        public List<Connection> Connections { get; set; }
        public List<LinkedApp> Apps { get; set; }

        public Settings()
        {
            Connections = new List<Connection>();
            Apps = new List<LinkedApp>();
        }

        public Connection FindConnection(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Connections) if (c.Id == id) return c;
            return null;
        }

        /// The next free local port, starting at 8081, that no connection uses yet.
        public int NextFreePort()
        {
            int port = 8081;
            while (true)
            {
                bool used = false;
                foreach (var c in Connections) if (c.Port == port) { used = true; break; }
                if (!used) return port;
                port++;
            }
        }
    }

    public static class SettingsStore
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "netroute");
        public static readonly string FilePath = Path.Combine(Dir, "settings.json");

        public static bool Exists()
        {
            return File.Exists(FilePath);
        }

        public static Settings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var json = File.ReadAllText(FilePath);
                var s = new JavaScriptSerializer().Deserialize<Settings>(json);
                if (s == null) return null;
                if (s.Connections == null) s.Connections = new List<Connection>();
                if (s.Apps == null) s.Apps = new List<LinkedApp>();
                foreach (var c in s.Connections)
                {
                    if (c.Name == null) c.Name = "";
                    if (c.Adapter == null) c.Adapter = "";
                    if (c.UpstreamProxy == null) c.UpstreamProxy = "";
                    if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString("N");
                }
                foreach (var a in s.Apps)
                {
                    if (a.Name == null) a.Name = "";
                    if (a.Path == null) a.Path = "";
                    if (a.Args == null) a.Args = "";
                    if (a.ConnectionId == null) a.ConnectionId = "";
                    if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Write("Could not read settings: " + ex.Message);
                return null;
            }
        }

        public static void Save(Settings s)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = new JavaScriptSerializer().Serialize(s);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Log.Write("Could not save settings: " + ex.Message);
            }
        }
    }

    /// A tiny log that the window shows at the bottom. Everything also goes to
    /// %LOCALAPPDATA%\netroute\netroute.log (debug lines only go to the file).
    public static class Log
    {
        public static event Action<string> Line;
        static readonly object gate = new object();
        static readonly string FilePath = Path.Combine(SettingsStore.Dir, "netroute.log");
        static bool started;

        public static void Write(string text)
        {
            var stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            ToFile(stamped);
            var h = Line;
            if (h != null) h(stamped);
        }

        public static void Debug(string text)
        {
            ToFile(DateTime.Now.ToString("HH:mm:ss.fff") + "  [dbg] " + text);
        }

        static void ToFile(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(SettingsStore.Dir);
                    if (!started)
                    {
                        started = true;
                        try { if (new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath); } catch { }
                        File.AppendAllText(FilePath, "\r\n==== netroute start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====\r\n");
                    }
                    File.AppendAllText(FilePath, line + "\r\n");
                }
            }
            catch { }
        }
    }
}
