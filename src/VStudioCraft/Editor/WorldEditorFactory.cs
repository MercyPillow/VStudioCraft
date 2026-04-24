using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace VStudioCraft.Editor
{
    [Guid(PackageGuids.EditorFactoryGuidString)]
    public sealed class WorldEditorFactory : IVsEditorFactory, IDisposable
    {
        public const string Extension = ".voxworld";

        private readonly VStudioCraftPackage _package;
        private ServiceProvider _serviceProvider;

        public WorldEditorFactory(VStudioCraftPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public int SetSite(IOleServiceProvider psp)
        {
            _serviceProvider = new ServiceProvider(psp);
            return VSConstants.S_OK;
        }

        public int Close()
        {
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }

        public int MapLogicalView(ref Guid rguidLogicalView, out string pbstrPhysicalView)
        {
            pbstrPhysicalView = null;
            if (rguidLogicalView == VSConstants.LOGVIEWID_Primary ||
                rguidLogicalView == VSConstants.LOGVIEWID_Designer)
            {
                return VSConstants.S_OK;
            }
            return VSConstants.E_NOTIMPL;
        }

        public int CreateEditorInstance(
            uint grfCreateDoc,
            string pszMkDocument,
            string pszPhysicalView,
            IVsHierarchy pvHier,
            uint itemid,
            IntPtr punkDocDataExisting,
            out IntPtr ppunkDocView,
            out IntPtr ppunkDocData,
            out string pbstrEditorCaption,
            out Guid pguidCmdUI,
            out int pgrfCDW)
        {
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = string.Empty;
            pguidCmdUI = PackageGuids.EditorFactoryGuid;
            pgrfCDW = 0;

            if (punkDocDataExisting != IntPtr.Zero)
            {
                return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
            }

            var pane = new WorldEditorPane(_package, pszMkDocument);
            ppunkDocView = Marshal.GetIUnknownForObject(pane);
            ppunkDocData = Marshal.GetIUnknownForObject(pane);
            pbstrEditorCaption = System.IO.Path.GetFileName(pszMkDocument);
            return VSConstants.S_OK;
        }
    }
}
