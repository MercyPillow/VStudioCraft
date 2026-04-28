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
            Host.ConnectFailed += ex =>
            {
                MessageBox.Show(this,
                    $"Failed to connect to server.\n\n{ex.Message}",
                    "Connection failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            };
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
            }
            Host.StartNewWorld(RandomSeed());
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
            var dlg = new OpenFileDialog { Filter = Filter, DefaultExt = Extension };
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
            var dlg = new SaveFileDialog
            {
                Filter = Filter,
                DefaultExt = Extension,
                FileName = _currentPath ?? ("world" + Extension),
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
