using System;
using System.Windows;
using Microsoft.Win32;

namespace VStudioCraft.Standalone
{
    public partial class MainWindow : Window
    {
        private const string Extension = ".voxworld";
        private const string Filter = "VStudioCraft world (*.voxworld)|*.voxworld|All files (*.*)|*.*";

        private string _currentPath;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnWindowLoaded;
            Closing += (_, __) => Host.Shutdown();
            // Pause-menu hooks. Save reuses the standard Save / Save-As path
            // so the user gets a dialog when there's no current world file.
            // Quit closes the window, mirroring the File → Exit menu item.
            Host.SaveRequested += () => OnSave(this, null);
            Host.QuitRequested += () => Close();
            // Phase 2c — surface multiplayer connect failures to the user
            // via a modal dialog. Without this hook, a refused / mistyped
            // server address silently drops back to a fresh SP world.
            //
            // Tier 6 #47 — When the connect failure originated from the
            // main-menu Multiplayer screen, bounce back there with the
            // error displayed in the screen's error line so the user can
            // edit and retry without losing context. The renderer's
            // ConnectToServer fallback runs ahead of this, but the title
            // re-open here puts the menu back on top and the error
            // becomes part of the natural retry flow.
            Host.ConnectFailed += ex =>
            {
                Host.ShowMultiplayerConnectError(ex.Message);
            };
            // Tier 6 #47 — Pause-menu Quit returns to the title screen
            // instead of closing the window in Standalone. VSIX still
            // wires Quit → close-pane via a separate event.
            Host.ReturnedToTitleRequested += () =>
            {
                Host.OpenTitleScreen();
                Host.ReleaseMouseLookExternal();
            };
            // Tier 6 #47 — Mirror the host's known world path into our
            // _currentPath so a Pause→Save lands at the file the user
            // chose at creation/load instead of falling through to
            // Save-As. Fired on Load / Create / Save / external SaveAs.
            Host.WorldPathChanged += path =>
            {
                _currentPath = string.IsNullOrEmpty(path) ? null : path;
            };
        }

        // Tier 6 #47 — Default save / open directory shared between
        // the title-screen World Select flow and the File menu's
        // dialogs. Matches the layout EnumerateSaves scans, so the
        // two paths agree on where worlds live by default and the
        // user doesn't have to navigate back to %APPDATA% on every
        // save-as.
        private static string DefaultSavesDirectory()
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VStudioCraft", "saves");
            try { System.IO.Directory.CreateDirectory(dir); } catch { /* read-only fs */ }
            return dir;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // --connect host:port[:username] launches straight into a
            // multiplayer session, bypassing the SP world auto-start.
            // Useful for dev / CI / docs; a proper "Connect" menu item
            // will follow once Phase 3 makes the session actually
            // playable. Username defaults to the local Windows username
            // so casual testing doesn't need extra typing.
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                string spec = null;
                if (a.StartsWith("--connect=", StringComparison.Ordinal))
                {
                    spec = a.Substring("--connect=".Length);
                }
                else if (a == "--connect" && i + 1 < args.Length)
                {
                    spec = args[i + 1];
                }
                if (!string.IsNullOrEmpty(spec))
                {
                    if (TryParseConnectSpec(spec, out var host, out var port, out var user))
                    {
                        Host.ConnectToServer(host, port, user);
                        return;
                    }
                    MessageBox.Show(this,
                        $"Invalid --connect value: {spec}\nExpected host:port or host:port:username",
                        "Bad command line", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }
                // Phase 7 — `--openlan[=PORT]` starts a fresh SP world and
                // immediately opens it to LAN on the chosen port (default
                // 25566). Useful for dev / CI to spin up a host process
                // without clicking through the title screen + pause menu.
                if (a == "--openlan" || a.StartsWith("--openlan=", StringComparison.Ordinal))
                {
                    int lanPort = VStudioCraft.Net.ServerHub.DefaultPort;
                    if (a.StartsWith("--openlan=", StringComparison.Ordinal))
                    {
                        var v = a.Substring("--openlan=".Length);
                        if (!int.TryParse(v, out lanPort)) lanPort = VStudioCraft.Net.ServerHub.DefaultPort;
                    }
                    Host.LanOpened   += p   => Title = $"VStudioCraft (hosting on :{p})";
                    Host.LanOpenFailed += ex => MessageBox.Show(this,
                        $"OpenToLan failed: {ex.Message}", "LAN open failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    Host.StartNewWorld(RandomSeed());
                    Host.OpenToLan(lanPort, Environment.UserName);
                    return;
                }
            }
            // Tier 6 #47 — Default Standalone path: open the title screen
            // and let the player pick what to do. Replaces the prior
            // auto-StartNewWorld so a fresh launch never silently
            // creates a throwaway world.
            Host.OpenTitleScreen();
        }

        // host:port               -> host, port, $env:USERNAME
        // host:port:username      -> host, port, username
        // Defaults port to 25566 (ServerHub.DefaultPort) if omitted entirely
        // ("hostonly" form), so "--connect localhost" Just Works against a
        // server started with default args.
        private static bool TryParseConnectSpec(string spec, out string host, out int port, out string user)
        {
            host = null; port = 25566; user = Environment.UserName ?? "player";
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var parts = spec.Split(':');
            if (parts.Length == 0 || parts.Length > 3) return false;
            host = parts[0];
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (parts.Length >= 2 && !int.TryParse(parts[1], out port)) return false;
            if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])) user = parts[2];
            return port > 0 && port <= 65535;
        }

        private static int RandomSeed() => (int)(DateTime.Now.Ticks & 0x7FFFFFFF);

        private void OnNew(object sender, RoutedEventArgs e)
        {
            _currentPath = null;
            Host.StartNewWorld(RandomSeed());
        }

        private void OnOpen(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = Filter,
                DefaultExt = Extension,
                // Tier 6 #47 — Default to the saves directory the
                // title-screen World Select flow scans, so File→Open
                // and the in-game World Select see the same world list
                // by default. The user can still navigate elsewhere.
                InitialDirectory = DefaultSavesDirectory(),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                Host.LoadFromFile(dlg.FileName);
                _currentPath = dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath)) { OnSaveAs(sender, e); return; }
            try { Host.SaveToFile(_currentPath); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OnSaveAs(object sender, RoutedEventArgs e)
        {
            // Tier 6 #47 — Default the dialog to the same saves
            // directory the title-screen World Select scans. If
            // _currentPath is already set we put its filename + parent
            // dir straight into the dialog so the user just confirms;
            // otherwise the saves dir + a placeholder name.
            string initialDir = string.IsNullOrEmpty(_currentPath)
                ? DefaultSavesDirectory()
                : (System.IO.Path.GetDirectoryName(_currentPath) ?? DefaultSavesDirectory());
            string initialName = string.IsNullOrEmpty(_currentPath)
                ? "world" + Extension
                : System.IO.Path.GetFileName(_currentPath);
            var dlg = new SaveFileDialog
            {
                Filter = Filter,
                DefaultExt = Extension,
                FileName = initialName,
                InitialDirectory = initialDir,
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                Host.SaveToFile(dlg.FileName);
                _currentPath = dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnExit(object sender, RoutedEventArgs e) => Close();
    }
}
