using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VStudioCraft.UI;

namespace VStudioCraft.Editor
{
    [ComVisible(true)]
    public sealed class WorldEditorPane : WindowPane, IVsPersistDocData
    {
        private readonly string _initialPath;
        private string _currentPath;
        private GameHostControl _control;
        private bool _isDirty;

        public WorldEditorPane(VStudioCraftPackage package, string filePath) : base(null)
        {
            _initialPath = filePath;
            _currentPath = filePath;
        }

        protected override void Initialize()
        {
            base.Initialize();
            _control = new GameHostControl();
            _control.Modified += OnControlModified;

            if (!string.IsNullOrEmpty(_initialPath) && File.Exists(_initialPath) && new FileInfo(_initialPath).Length > 0)
            {
                _control.LoadFromFile(_initialPath);
            }
            else
            {
                _control.StartNewWorld(DefaultSeed());
            }

            Content = _control;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_control != null) _control.Modified -= OnControlModified;
                _control?.Shutdown();
                _control = null;
            }
            base.Dispose(disposing);
        }

        private static int DefaultSeed() => (int)(DateTime.Now.Ticks & 0x7FFFFFFF);

        private void OnControlModified()
        {
            _isDirty = true;
        }

        public int GetGuidEditorType(out Guid pClassID)
        {
            pClassID = PackageGuids.EditorFactoryGuid;
            return VSConstants.S_OK;
        }

        public int IsDocDataDirty(out int pfDirty)
        {
            pfDirty = _isDirty ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int SetUntitledDocPath(string pszDocDataPath)
        {
            _currentPath = pszDocDataPath;
            return VSConstants.S_OK;
        }

        public int LoadDocData(string pszMkDocument)
        {
            _currentPath = pszMkDocument;
            try
            {
                if (_control != null)
                {
                    if (File.Exists(pszMkDocument) && new FileInfo(pszMkDocument).Length > 0)
                        _control.LoadFromFile(pszMkDocument);
                    else
                        _control.StartNewWorld(DefaultSeed());
                }
                _isDirty = false;
                return VSConstants.S_OK;
            }
            catch
            {
                return VSConstants.E_FAIL;
            }
        }

        public int SaveDocData(VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            pbstrMkDocumentNew = _currentPath;
            pfSaveCanceled = 0;
            try
            {
                _control?.SaveToFile(_currentPath);
                _isDirty = false;
                return VSConstants.S_OK;
            }
            catch
            {
                return VSConstants.E_FAIL;
            }
        }

        public int Close() => VSConstants.S_OK;

        public int OnRegisterDocData(uint docCookie, IVsHierarchy pHierHierarchy, uint itemidNew) => VSConstants.S_OK;

        public int RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        {
            _currentPath = pszMkDocumentNew;
            return VSConstants.S_OK;
        }

        public int IsDocDataReloadable(out int pfReloadable)
        {
            pfReloadable = 1;
            return VSConstants.S_OK;
        }

        public int ReloadDocData(uint grfFlags) => LoadDocData(_currentPath);
    }
}
