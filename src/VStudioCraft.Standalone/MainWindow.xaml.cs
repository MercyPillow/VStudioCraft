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
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Host.StartNewWorld(RandomSeed());
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
