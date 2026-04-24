using System;
using System.ComponentModel.Design;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using Task = System.Threading.Tasks.Task;

namespace VStudioCraft.Commands
{
    internal static class OpenWorldCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (!(await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs))
            {
                return;
            }

            var cmdId = new CommandID(PackageGuids.CommandSet, PackageIds.OpenWorldCommandId);
            mcs.AddCommand(new MenuCommand((_, __) => Execute(package), cmdId));
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var savesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VStudioCraft",
                "saves");
            Directory.CreateDirectory(savesDir);

            var dlg = new OpenFileDialog
            {
                Title = "Open Voxel World",
                Filter = "Voxel World (*.voxworld)|*.voxworld|All files (*.*)|*.*",
                InitialDirectory = savesDir,
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dlg.ShowDialog() != true) return;

            VsShellUtilities.OpenDocumentWithSpecificEditor(
                package,
                dlg.FileName,
                PackageGuids.EditorFactoryGuid,
                VSConstants.LOGVIEWID_Primary,
                out _,
                out _,
                out var frame);
            frame?.Show();
        }
    }
}
