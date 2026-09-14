using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        int _sourceBrowseMode;
        string _sourceUsbRoot = "", _sourceUsbPath = "", _communityAfter = "", _communityNext = "", _sourceBrowseError = "";
        List<string> _sourcePaths = new List<string>();
        Dictionary<string, string> _sourceUsbNames = new Dictionary<string, string>(StringComparer.Ordinal);
        List<CommunitySourceEntry> _communityEntries = new List<CommunitySourceEntry>();
        readonly Stack<string> _communityPages = new Stack<string>();
        volatile bool _sourceBrowserBusy;
        Action _sourceBrowserComplete;

        string SourceUsbName(string mount)
        {
            string name;
            if (_sourceUsbNames.TryGetValue(mount, out name)) return name;
            return "USB drive" + (mount.Length == 9 ? " " + (mount[8] - '0' + 1) : "");
        }

        void BrowseSourceUsb(string directory)
        {
            _sourcePaths.Clear(); _sourceBrowseError = ""; _settingsFocus = 0;
            _sourceBrowseMode = string.IsNullOrEmpty(directory) ? 1 : 2; _sourceUsbPath = directory ?? "";
            try {
                if (_sourceBrowseMode == 1) {
                    for (int i = 0; i < 8; i++) { string p = "/mnt/usb" + i; if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0) _sourcePaths.Add(p); }
                    _sourceUsbNames.Clear();
                    var mounts = _sourcePaths.ToArray();
                    _sourceBrowserBusy = true;
                    ThreadPool.QueueUserWorkItem(_ => {
                        var names = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (string mount in mounts) {
                            string label = UsbVolumeLabel.Read(mount);
                            names[mount] = label.Length > 0 ? label : "USB drive " + (mount[8] - '0' + 1);
                        }
                        Interlocked.Exchange(ref _sourceBrowserComplete, () => { _sourceUsbNames=names; _sourceBrowserBusy=false; Invalidated=true; });
                    });
                } else {
                    string full = Path.GetFullPath(directory);
                    if (full != _sourceUsbRoot && !full.StartsWith(_sourceUsbRoot + "/", StringComparison.Ordinal)) throw new IOException("Outside selected USB drive");
                    foreach (string p in Directory.EnumerateFileSystemEntries(full)) {
                        if (_sourcePaths.Count >= 250) break;
                        if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) continue;
                        if (Directory.Exists(p) || p.EndsWith(".gssource", StringComparison.OrdinalIgnoreCase)) _sourcePaths.Add(p);
                    }
                    _sourcePaths.Sort(StringComparer.OrdinalIgnoreCase);
                }
            } catch { _sourceBrowseError = "USB unavailable. Reconnect the drive and refresh."; }
            Invalidated = true;
        }
        void BrowseCommunity(string after)
        {
            if (_sourceBrowserBusy) return;
            _sourceBrowseMode = 3; _settingsFocus = 0; _sourceBrowserBusy = true; _sourceBrowseError = "";
            ThreadPool.QueueUserWorkItem(_ => {
                try { string next; var rows = CommunitySources.List(after, out next);
                    Interlocked.Exchange(ref _sourceBrowserComplete, () => { _communityEntries = rows; _communityAfter=after; _communityNext=next; _sourceBrowserBusy=false; Invalidated=true; });
                } catch { Interlocked.Exchange(ref _sourceBrowserComplete, () => { _sourceBrowserBusy=false; _communityEntries.Clear(); _communityNext=""; _sourceBrowseError="Community directory unavailable. Triangle retries."; Invalidated=true; }); }
            });
        }
        bool SourceBrowserBack()
        {
            if (_sourceBrowseMode == 0) return false;
            if (_sourceBrowserBusy) { SetStatus("Source operation is still working"); return true; }
            if (_sourceBrowseMode == 2) {
                if (_sourceUsbPath != _sourceUsbRoot) BrowseSourceUsb(Path.GetDirectoryName(_sourceUsbPath)); else BrowseSourceUsb(null);
            } else if (_sourceBrowseMode == 3 && _communityPages.Count > 0) BrowseCommunity(_communityPages.Pop());
            else { _sourceBrowseMode=0; _settingsFocus=0; RefreshSourceUi(); }
            return true;
        }
        void HandleSourceBrowser(DS4Button b)
        {
            if (_sourceBrowserBusy) return;
            if (b == DS4Button.SCE_PAD_BUTTON_TRIANGLE) { if (_sourceBrowseMode==3) BrowseCommunity(_communityAfter);else BrowseSourceUsb(_sourceBrowseMode==1?null:_sourceUsbPath);return; }
            int count = _sourceBrowseMode==3 ? _communityEntries.Count + (_communityNext.Length>0?1:0) : _sourcePaths.Count;
            if (count==0) return;
            if (b==DS4Button.SCE_PAD_BUTTON_UP) { _settingsFocus=(_settingsFocus+count-1)%count;return; }
            if (b==DS4Button.SCE_PAD_BUTTON_DOWN) { _settingsFocus=(_settingsFocus+1)%count;return; }
            if (b!=DS4Button.SCE_PAD_BUTTON_CROSS) return;
            if (_sourceBrowseMode==3) {
                if (_settingsFocus==_communityEntries.Count) { _communityPages.Push(_communityAfter);BrowseCommunity(_communityNext);return; }
                var entry=_communityEntries[_settingsFocus]; InstallBrowserSource(null,entry);
            } else {
                string path=_sourcePaths[_settingsFocus];
                if (Directory.Exists(path)) { if (_sourceBrowseMode==1) _sourceUsbRoot=path;BrowseSourceUsb(path); }
                else InstallBrowserSource(path,null);
            }
        }
        void InstallBrowserSource(string path, CommunitySourceEntry entry)
        {
            if (_sourceBrowserBusy || SourceInstallActive(GetSourceInstallView().Stage)) return;
            _sourceBrowserBusy=true; BeginSourceInstall(new Uri(CommunitySources.Endpoint));
            UpdateSourceInstall(SourceInstallStage.Downloading,0,0,entry==null?"Reading USB source...":"Receiving encrypted source...");
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    byte[] bytes=entry!=null?CommunitySources.Download(entry):null;
                    UpdateSourceInstall(SourceInstallStage.Validating,0,0,"Checking source format and limits...");
                    string error;bool installed=bytes!=null?_packageSources.Install(bytes,out error):_packageSources.Install(path,out error);
                    if (!installed) throw new IOException(error);
                    Interlocked.Exchange(ref _sourceBrowserComplete, () => { _sourceBrowserBusy=false;UpdateSourceInstall(SourceInstallStage.Complete,1,1,"Source installed and ready");SourcesChanged();SetStatus("Source installed and ready");Invalidated=true; });
                } catch(Exception ex) {
                    string error=Clip(ex.Message,120);
                    Interlocked.Exchange(ref _sourceBrowserComplete, () => { _sourceBrowserBusy=false;UpdateSourceInstall(SourceInstallStage.Failed,0,0,error);SetStatus("Source could not be installed: "+error);Invalidated=true; });
                }
            });
        }
        void DrawSourceBrowser(IntPtr r, SDL_Rect sheet)
        {
            int x=sheet.x,w=sheet.w;bool community=_sourceBrowseMode==3;
            TextPx(r,x,sheet.y+15,28,community?"Community sources":"Install source from USB",White);
            string usbName=SourceUsbName(_sourceUsbRoot);
            string usbLocation = usbName + (_sourceUsbPath.Length>_sourceUsbRoot.Length ? " / " + _sourceUsbPath.Substring(_sourceUsbRoot.Length).TrimStart('/') : "");
            TextPx(r,x,sheet.y+60,18,community?"Shared by the community · sources are checked before installation":_sourceBrowseMode==1?"Choose a connected USB drive":Clip(usbLocation,100),Muted);
            if (_sourceBrowserBusy) { TextPx(r,x,sheet.y+130,23,"Please wait...",Muted);return; }
            int count=community?_communityEntries.Count+(_communityNext.Length>0?1:0):_sourcePaths.Count;
            int start=Math.Max(0,_settingsFocus-4);
            for(int i=0;i<5 && start+i<count;i++) {
                int n=start+i;string name,detail,action;
                if(community && n<_communityEntries.Count) {var e=_communityEntries[n];name=e.Name.Length>0?e.Name:"Community source";detail=Clip(e.Tags+" · "+e.Date+" · "+(e.Size/1024)+" KiB",100);action="Install";}
                else if(community){name="Next page";detail="More community sources";action="Browse";}
                else {string p=_sourcePaths[n];name=_sourceBrowseMode==1?SourceUsbName(p):Path.GetFileName(p);detail=_sourceBrowseMode==1?"USB drive "+(p[8]-'0'+1)+" · Browse source files":Directory.Exists(p)?"Open folder":"Package source file";action=Directory.Exists(p)?"Open":"Install";}
                DrawSettingsRow(r,x,sheet.y+115+i*97,w,86,n,name,detail,action);
            }
            if(count==0)TextPx(r,x,sheet.y+160,23,_sourceBrowseError.Length>0?_sourceBrowseError:community?DistributionSettings.EmptyDirectoryMessage:"No sources found. Connect a USB drive with .gssource files.",Muted);
            if(community && _settingsFocus<_communityEntries.Count) {
                var e=_communityEntries[_settingsFocus];TextPx(r,x,sheet.y+630,17,Clip(e.Message,135),Muted);
                TextPx(r,x,sheet.y+665,14,DistributionSettings.ReportLabel+e.Id,Dim);
            }
        }
    }
}
