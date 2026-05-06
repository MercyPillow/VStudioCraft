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

            // Tier 6 #47 — VSIX editor pane opens to the title screen
            // instead of auto-loading the bound file. Single Player on
            // the menu loads the bound file (PlayBoundFile below);
            // Multiplayer / Settings work the same as Standalone. The
            // World Select submenu is skipped because the pane is
            // already bound to one specific file — re-prompting via a
            // saves picker would conflict with the VS document model.
            _control.SetSinglePlayerHandler(PlayBoundFile);
            // ReturnedToTitleRequested fires when the user picks
            // Pause → Quit; in the editor pane that should bounce the
            // player back to the title screen so they can switch to
            // multiplayer mid-session, same as Standalone.
            _control.ReturnedToTitleRequested += () =>
            {
                _control?.OpenTitleScreen();
                _control?.ReleaseMouseLookExternal();
            };
            // QuitRequested fires when the user clicks Quit on the
            // title screen. Standalone wires this to Window.Close()
            // (terminates the .exe). Inside the VS shell we DO NOT
            // want to close the owning window — that's the VS main
            // window, which would shut Visual Studio itself. Instead
            // close just this editor frame (the document tab), which
            // also disposes the pane and shuts the embedded game
            // host down via Dispose. FRAMECLOSE_SaveIfDirty triggers
            // a save prompt on unsaved changes, matching the rest of
            // VS's "close-tab" semantics.
            _control.QuitRequested += () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var frame = GetService(typeof(SVsWindowFrame)) as IVsWindowFrame;
                frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_SaveIfDirty);
            };
            _control.OpenTitleScreen();

            Content = _control;
        }

        // Tier 6 #47 — Single-Player click handler for the VSIX-bound
        // pane. Loads the file the pane was opened on, or starts a
        // fresh world when the file is empty / missing (same fallback
        // the original auto-load path used). Called from the title
        // screen's Single Player button via SetSinglePlayerHandler.
        private void PlayBoundFile()
        {
            if (_control == null) return;
            if (!string.IsNullOrEmpty(_initialPath) && File.Exists(_initialPath) && new FileInfo(_initialPath).Length > 0)
            {
                _control.LoadFromFile(_initialPath);
            }
            else
            {
                _control.StartNewWorld(DefaultSeed());
            }
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
                    // Tier 6 #47 — VS-driven file load (initial open
                    // from Solution Explorer, file reload after an
                    // external edit). Skip the title screen for this
                    // path — VS is explicitly asking us to load the
                    // file, and forcing the menu in front would feel
                    // like the editor ignored the request. Dismiss
                    // the menu if it happens to be up + capture mouse
                    // so the user lands in FPS view as before.
                    if (File.Exists(pszMkDocument) && new FileInfo(pszMkDocument).Length > 0)
                        _control.LoadFromFile(pszMkDocument);
                    else
                        _control.StartNewWorld(DefaultSeed());
                    _control.DismissTitleAndCaptureMouse();
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
