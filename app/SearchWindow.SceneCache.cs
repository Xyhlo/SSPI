using System;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        IntPtr _sceneTexture, _scenePixels;
        bool _sceneValid, _sceneFailed;
        int _landingRevision, _sceneRevision, _sceneArtworkRevision, _scenePairRevision;
        uint _sceneInput;
        int _sceneFocus, _sceneExpandedFocus;
        bool _sceneExpanded;
        IntPtr _scenePattern, _sceneBackdrop;

        bool CanCacheScene()
        {
            return !_sceneFailed && !_settingsOpen && !_softKbOpen && _uiOverlay == UiOverlay.None &&
                _tab == TopTab.Search && _screen == BrowseScreen.Search && UiElapsed(_lastInputAt) >= 220;
        }

        bool DrawCachedScene(IntPtr r)
        {
            if (_libraryOpen && _libraryCaptureAttempted) PumpLibraryBackdrop(r);
            if (!CanCacheScene() || !_sceneValid || _sceneInput != _lastInputAt ||
                _sceneRevision != _landingRevision || _sceneArtworkRevision != _covers.Revision ||
                _scenePairRevision != _pair.Revision || _sceneFocus != _searchLandingFocus ||
                _sceneExpandedFocus != _expandedLibraryFocus || _sceneExpanded != _libraryOpen ||
                _scenePattern != _patternTexture || _sceneBackdrop != _libraryBackdrop) {
                _sceneValid = false; return false;
            }
            var destination = new SDL_Rect { x = 0, y = 106, w = W, h = 880 };
            SDL_RenderCopy(r, _sceneTexture, IntPtr.Zero, ref destination);
            return true;
        }

        void CacheScene(IntPtr r)
        {
            if (!CanCacheScene() || _patternBusy || _patternUploadPixels != null || _patternPixels != null ||
                (_libraryOpen && _libraryBackdrop == IntPtr.Zero)) return;
            if (_sceneTexture == IntPtr.Zero) {
                _sceneTexture = SDL_CreateTexture(r, SDL_PIXELFORMAT_BGR888, (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, W, 880);
                if (_sceneTexture == IntPtr.Zero) { _sceneFailed = true; return; }
                SDL_SetTextureBlendMode(_sceneTexture, SDL_BlendMode.SDL_BLENDMODE_NONE);
                _scenePixels = Marshal.AllocHGlobal(W * 880 * 4);
            }
            var source = new SDL_Rect { x = 0, y = 106, w = W, h = 880 };
            if (SDL_RenderReadPixels(r, ref source, SDL_PIXELFORMAT_BGR888, _scenePixels, W * 4) != 0 ||
                SDL_UpdateTexture(_sceneTexture, IntPtr.Zero, _scenePixels, W * 4) != 0) {
                _sceneFailed = true; ReleaseSceneCache(); return;
            }
            _sceneInput = _lastInputAt; _sceneRevision = _landingRevision; _sceneArtworkRevision = _covers.Revision;
            _scenePairRevision = _pair.Revision; _sceneFocus = _searchLandingFocus;
            _sceneExpandedFocus = _expandedLibraryFocus; _sceneExpanded = _libraryOpen;
            _scenePattern = _patternTexture; _sceneBackdrop = _libraryBackdrop; _sceneValid = true;
        }

        void ReleaseSceneCache()
        {
            if (_sceneTexture != IntPtr.Zero) SDL_DestroyTexture(_sceneTexture);
            if (_scenePixels != IntPtr.Zero) Marshal.FreeHGlobal(_scenePixels);
            _sceneTexture = _scenePixels = IntPtr.Zero; _sceneValid = false;
        }
    }
}
