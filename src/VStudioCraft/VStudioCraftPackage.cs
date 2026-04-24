using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VStudioCraft
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.PackageGuidString)]
    [ProvideEditorFactory(typeof(Editor.WorldEditorFactory), 200, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(Editor.WorldEditorFactory), Editor.WorldEditorFactory.Extension, 32)]
    public sealed class VStudioCraftPackage : AsyncPackage
    {
        private static readonly string ExtensionFolder;

        static VStudioCraftPackage()
        {
            ExtensionFolder = Path.GetDirectoryName(typeof(VStudioCraftPackage).Assembly.Location) ?? string.Empty;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            if (requested != "OpenTK" && requested != "OpenTK.GLControl") return null;

            var candidate = Path.Combine(ExtensionFolder, requested + ".dll");
            if (!File.Exists(candidate)) return null;

            try { return Assembly.LoadFrom(candidate); }
            catch { return null; }
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            RegisterEditorFactory(new Editor.WorldEditorFactory(this));

            await Commands.NewWorldCommand.InitializeAsync(this);
            await Commands.OpenWorldCommand.InitializeAsync(this);
        }
    }
}
