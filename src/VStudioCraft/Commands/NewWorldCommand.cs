using System;
using System.ComponentModel.Design;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace VStudioCraft.Commands
{
    internal static class NewWorldCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (!(await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs))
            {
                return;
            }

            var cmdId = new CommandID(PackageGuids.CommandSet, PackageIds.NewWorldCommandId);
            mcs.AddCommand(new MenuCommand((_, __) => Execute(package), cmdId));
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VStudioCraft",
                "saves");
            Directory.CreateDirectory(dir);

            string path;
            int n = 1;
            do
            {
                path = Path.Combine(dir, $"World{n}.voxworld");
                n++;
            } while (File.Exists(path));

            File.WriteAllBytes(path, Array.Empty<byte>());

            VsShellUtilities.OpenDocumentWithSpecificEditor(
                package,
                path,
                PackageGuids.EditorFactoryGuid,
                VSConstants.LOGVIEWID_Primary,
                out _,
                out _,
                out var frame);
            frame?.Show();
        }
    }
}
