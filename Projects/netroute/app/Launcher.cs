// Launch an app so that it uses one connection's proxy.
//
// Browsers accept a proxy on the command line, so no system-wide setting is
// touched and two browsers can sit on two different connections at once.
// Each app gets its own browser profile folder, which also keeps it a separate
// process from any copy of the same browser the user already has open (a new
// window of an already-running browser would ignore the proxy flag).
//
//   Chromium family (Edge, Chrome, Brave, Opera, Vivaldi, Electron apps):
//       --proxy-server=127.0.0.1:PORT  --user-data-dir=<profile>
//   Firefox family:
//       a profile folder with user.js that sets the proxy, -no-remote
//   Anything else:
//       HTTP_PROXY / HTTPS_PROXY / ALL_PROXY environment variables, which many
//       command-line tools and some apps honour. Apps that ignore them use the
//       Windows default connection; netroute cannot force those without a driver.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NetRoute
{
    public static class Launcher
    {
        public static readonly string ProfilesRoot = Path.Combine(SettingsStore.Dir, "profiles");

        static readonly string[] Chromium = { "msedge.exe", "chrome.exe", "brave.exe", "chromium.exe", "opera.exe", "vivaldi.exe", "discord.exe", "slack.exe", "code.exe", "teams.exe", "spotify.exe", "claude.exe", "chatgpt.exe", "obsidian.exe", "notion.exe", "signal.exe", "whatsapp.exe" };
        static readonly string[] Firefox = { "firefox.exe", "librewolf.exe", "waterfox.exe", "tor.exe" };

        public enum Kind { Chromium, Firefox, Other }

        public static Kind KindOf(string path)
        {
            var exe = Path.GetFileName(path ?? "").ToLowerInvariant();
            foreach (var c in Chromium) if (exe == c) return Kind.Chromium;
            foreach (var f in Firefox) if (exe == f) return Kind.Firefox;
            if (LooksLikeElectron(path)) return Kind.Chromium;
            return Kind.Other;
        }

        /// Electron apps carry Chromium's files next to the exe and accept --proxy-server.
        static bool LooksLikeElectron(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return false;
                if (File.Exists(Path.Combine(dir, "resources", "app.asar"))) return true;
                if (File.Exists(Path.Combine(dir, "v8_context_snapshot.bin"))) return true;
                if (File.Exists(Path.Combine(dir, "chrome_100_percent.pak"))) return true;
                // Store-installed apps keep the real exe one folder down.
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, "resources", "app.asar"))) return true;
                    if (File.Exists(Path.Combine(sub, "v8_context_snapshot.bin"))) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsBrowser(string path)
        {
            var exe = Path.GetFileName(path ?? "").ToLowerInvariant();
            return exe == "msedge.exe" || exe == "chrome.exe" || exe == "brave.exe" || exe == "chromium.exe"
                || exe == "opera.exe" || exe == "vivaldi.exe" || KindOf(path) == Kind.Firefox;
        }

        /// Human-readable description of what Launch will run, for the log.
        public static string Describe(LinkedApp app, Connection conn)
        {
            string args = BuildArgs(app, conn, false);
            return "\"" + app.Path + "\" " + args;
        }

        public static Process Launch(LinkedApp app, Connection conn)
        {
            if (!File.Exists(app.Path)) throw new FileNotFoundException("App not found: " + app.Path);
            var psi = new ProcessStartInfo(app.Path);
            psi.UseShellExecute = false;
            psi.Arguments = BuildArgs(app, conn, true);
            try { psi.WorkingDirectory = Path.GetDirectoryName(app.Path); } catch { }

            string proxy = "http://127.0.0.1:" + conn.Port;
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                psi.EnvironmentVariables[name] = proxy;
            psi.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1";
            psi.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1";

            return Process.Start(psi);
        }

        static string BuildArgs(LinkedApp app, Connection conn, bool prepareProfile)
        {
            string proxy = "127.0.0.1:" + conn.Port;
            string profile = ProfileDir(app);
            var sb = new StringBuilder();
            switch (KindOf(app.Path))
            {
                case Kind.Chromium:
                    sb.Append("--proxy-server=").Append(proxy).Append(' ');
                    if (IsBrowser(app.Path))
                    {
                        if (prepareProfile) Directory.CreateDirectory(profile);
                        sb.Append("--user-data-dir=\"").Append(profile).Append("\" ");
                        sb.Append("--no-first-run --no-default-browser-check ");
                    }
                    break;
                case Kind.Firefox:
                    if (prepareProfile) WriteFirefoxProfile(profile, conn.Port);
                    sb.Append("-profile \"").Append(profile).Append("\" -no-remote ");
                    break;
                default:
                    break;
            }
            if (!string.IsNullOrEmpty(app.Args)) sb.Append(app.Args.Trim());
            return sb.ToString().Trim();
        }

        static string ProfileDir(LinkedApp app)
        {
            var safe = new StringBuilder();
            foreach (var ch in (app.Name ?? "app"))
                safe.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            return Path.Combine(ProfilesRoot, safe.ToString().Trim('-') + "-" + app.Id.Substring(0, 8));
        }

        static void WriteFirefoxProfile(string dir, int port)
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("// written by netroute on every launch; the proxy port follows the connection.");
            sb.AppendLine("user_pref(\"network.proxy.type\", 1);");
            sb.AppendLine("user_pref(\"network.proxy.http\", \"127.0.0.1\");");
            sb.AppendLine("user_pref(\"network.proxy.http_port\", " + port + ");");
            sb.AppendLine("user_pref(\"network.proxy.ssl\", \"127.0.0.1\");");
            sb.AppendLine("user_pref(\"network.proxy.ssl_port\", " + port + ");");
            sb.AppendLine("user_pref(\"network.proxy.share_proxy_settings\", true);");
            sb.AppendLine("user_pref(\"network.proxy.no_proxies_on\", \"\");");
            sb.AppendLine("user_pref(\"network.proxy.allow_hijacking_localhost\", false);");
            sb.AppendLine("user_pref(\"network.trr.mode\", 5);"); // no DNS-over-HTTPS bypass
            sb.AppendLine("user_pref(\"browser.shell.checkDefaultBrowser\", false);");
            sb.AppendLine("user_pref(\"browser.aboutwelcome.enabled\", false);");
            File.WriteAllText(Path.Combine(dir, "user.js"), sb.ToString());
        }

        /// Browsers found at their usual install locations, for the "Add browser" menu.
        public static System.Collections.Generic.List<LinkedApp> DetectBrowsers()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                new[] { "Microsoft Edge", Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe") },
                new[] { "Google Chrome", Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(local, @"Google\Chrome\Application\chrome.exe") },
                new[] { "Firefox", Path.Combine(pf, @"Mozilla Firefox\firefox.exe"), Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"), Path.Combine(local, @"Mozilla Firefox\firefox.exe") },
                new[] { "Brave", Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe") },
                new[] { "Opera", Path.Combine(local, @"Programs\Opera\opera.exe"), Path.Combine(pf, @"Opera\opera.exe") },
                new[] { "Vivaldi", Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"), Path.Combine(pf, @"Vivaldi\Application\vivaldi.exe") },
            };
            var found = new System.Collections.Generic.List<LinkedApp>();
            foreach (var c in candidates)
            {
                for (int i = 1; i < c.Length; i++)
                {
                    if (File.Exists(c[i]))
                    {
                        found.Add(new LinkedApp { Name = c[0], Path = c[i] });
                        break;
                    }
                }
            }
            return found;
        }
    }
}
