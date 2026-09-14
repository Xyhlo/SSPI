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
        bool _cloudMenu;
        bool _pairDownloadMode;
        int _storageView, _cloudPage, _cloudScroll, _cloudGeneration;
        string _cloudFolder="", _cloudMessage="", _directDraft="";
        volatile bool _cloudBusy;
        List<CloudFile> _cloudFiles=new List<CloudFile>();
        List<string> _stagingChoices=new List<string>{"ps4"};
        List<string> _stagingNames=new List<string>{"Default - PS4"};

        bool StorageBack()
        { if(_storageView==0)return false;_storageView=0;_settingsFocus=0;_storageDelete=null;return true; }
        void StorageMenu(DS4Button b)
        {
            if(_storageView==1){HandleStoredFiles(b);return;}
            if(_storageBusy)return;
            int count=_storageView==2?_stagingChoices.Count:2;
            if(b==DS4Button.SCE_PAD_BUTTON_UP)_settingsFocus=Math.Max(0,_settingsFocus-1);
            if(b==DS4Button.SCE_PAD_BUTTON_DOWN)_settingsFocus=Math.Min(count-1,_settingsFocus+1);
            if(b!=DS4Button.SCE_PAD_BUTTON_CROSS)return;
            if(_storageView==2)
            {
                if(!_dlMgr.CanChangeStaging())
                {_storageMessage="Finish or remove background jobs and wait for the worker to stop before changing drives.";User.NotifyToast("Stop background jobs before changing staging drives.");return;}
                if (_stagingChoices[_settingsFocus] != "ps4" && !UsbVolumeLabel.IsConnected(_stagingChoices[_settingsFocus]))
                {_storageMessage="Reconnect the selected USB drive, then open Staging location again.";User.NotifyToast(_storageMessage);return;}
                string error;
                if(_cfg.SelectStaging(_stagingChoices[_settingsFocus],out error))
                {_storageView=0;_settingsFocus=0;_storageLoaded=false;_storageMessage="Staging location saved";User.NotifyToast(_storageMessage);}
                else{_storageMessage=error;User.NotifyToast(Clip(error ?? "Staging location unavailable",130));}
                return;
            }
            if(_settingsFocus==1){_storageView=1;_settingsFocus=0;_storageLoaded=false;RefreshStorage();return;}
            _storageView=2;_settingsFocus=0;_storageBusy=true;
            ThreadPool.QueueUserWorkItem(_=>{
                var locations=new List<string>{"ps4"};var names=new List<string>{"Default - PS4"};
                for(int i=0;i<8;i++){string mount="/mnt/usb"+i;if(!UsbVolumeLabel.IsConnected(mount))continue;
                    try {if((File.GetAttributes(mount)&FileAttributes.ReparsePoint)!=0)continue;string name=UsbVolumeLabel.Read(mount);
                        locations.Add(mount);names.Add("Hard drive - "+(string.IsNullOrWhiteSpace(name)?"USB "+(i+1):name));}catch{}}
                lock(_lock){_stagingChoices=locations;_stagingNames=names;_storageBusy=false;Invalidated=true;}
            });
        }
        void DrawStorageMenu(IntPtr r,SDL_Rect sheet)
        {
            if(_storageView==1){DrawStoredFiles(r,sheet);return;}
            TextPx(r,sheet.x,sheet.y+15,28,_storageView==2?"Staging location":"Storage",White);
            TextFit(r,sheet.x,sheet.y+60,18,sheet.w,"Downloads and extraction stay here until installation succeeds.",Muted);
            if(_storageView==0)
            {
                DrawSettingsRow(r,sheet.x,sheet.y+116,sheet.w,92,0,"Staging location",_cfg.StagingLocation=="ps4"?"Default - PS4":"Hard drive - USB "+(_cfg.StagingLocation[8]-'0'+1),"Choose");
                DrawSettingsRow(r,sheet.x,sheet.y+228,sheet.w,92,1,"Stored files","Browse internal and connected USB files; remove unused files","Open");
                TextFit(r,sheet.x,sheet.y+362,19,sheet.w,"Confirmed installs remove their staging PKG and transfer sidecars. Failed jobs keep their files.",Muted);
                TextFit(r,sheet.x,sheet.y+405,18,sheet.w,"Use an exFAT USB drive for large packages. Keep it connected until installation finishes.",Dim);
            }
            else
            {
                if(_storageBusy)TextPx(r,sheet.x,sheet.y+120,22,"Reading connected drives...",Muted);
                else {int start=Math.Max(0,_settingsFocus-4);for(int i=start;i<_stagingChoices.Count&&i<start+5;i++)
                    DrawSettingsRow(r,sheet.x,sheet.y+112+(i-start)*96,sheet.w,84,i,_stagingNames[i],i==0?"Internal storage":"SSPI/staging - download and extract on this drive",_cfg.StagingLocation==_stagingChoices[i]?"Selected":"Use");}
            }
            TextFit(r,sheet.x,sheet.y+666,18,sheet.w,_storageMessage,Muted);
        }
        bool CloudBack()
        {
            if (_usbInstallOpen) return UsbInstallBack();
            ++_cloudGeneration;_cloudBusy=false;_cloudPage=0;_settingsFocus=0;
            if(_cloudFolder!=""){_cloudFolder="";_cloudFiles.Clear();_cloudMessage="";return true;}
            if(_cloudMenu){_cloudMenu=false;return true;}
            _settingsOpen=false;_settingsPage=0;return true;
        }
        void LoadCloud(string folder,int page)
        {
            if(_cloudBusy)return;_cloudFolder=folder;_cloudPage=Math.Max(0,page);_cloudBusy=true;_cloudMessage="Loading your files...";
            int generation=++_cloudGeneration;
            ThreadPool.QueueUserWorkItem(_=>{
                List<CloudFile> files=null;string message="";
                try{files=CloudCatalog.List(_cfg,folder,_cloudPage);if(files.Count==0)message="No files on this page. LEFT returns to the previous page.";}
                catch{message="Could not load cloud files. Check the service connection, then press TRIANGLE to retry.";}
                lock(_lock){if(generation!=_cloudGeneration)return;_cloudFiles=files??new List<CloudFile>();_cloudMessage=message;_settingsFocus=0;_cloudScroll=0;_cloudBusy=false;Invalidated=true;}
            });
        }
        void QueuePersonalLink(string name,string link,bool cloud)
        {
            if(!cloud&&!CloudCatalog.ValidLink(link))throw new IOException("Enter a complete HTTP or HTTPS file URL");
            string message;
            _dlMgr.Enqueue(new GameHit{TitleId="",Name=string.IsNullOrWhiteSpace(name)?"My package":name,ImageUrl=""},
                new PkgLink{Kind="package",Label="Personal file",Url=link},"personal","1","",cloud?"Cloud":"Personal","","",out message);
            _cloudMessage=message;User.NotifyToast(message);
        }
        void HandleMyFiles(DS4Button b)
        {
            if (_usbInstallOpen) { HandleUsbInstall(b); return; }
            if(_cloudBusy)return;
            int count=_cloudFolder==""?(_cloudMenu?4:3):_cloudFiles.Count;
            if(b==DS4Button.SCE_PAD_BUTTON_UP)_settingsFocus=Math.Max(0,_settingsFocus-1);
            if(b==DS4Button.SCE_PAD_BUTTON_DOWN)_settingsFocus=Math.Min(Math.Max(0,count-1),_settingsFocus+1);
            if(_cloudFolder!=""&&(b==DS4Button.SCE_PAD_BUTTON_TRIANGLE||b==DS4Button.SCE_PAD_BUTTON_LEFT||b==DS4Button.SCE_PAD_BUTTON_RIGHT))
            {LoadCloud(_cloudFolder,_cloudPage+(b==DS4Button.SCE_PAD_BUTTON_RIGHT?1:b==DS4Button.SCE_PAD_BUTTON_LEFT?-1:0));return;}
            if(b!=DS4Button.SCE_PAD_BUTTON_CROSS)return;
            if(_cloudFolder=="")
            {
                if(_cloudMenu){LoadCloud(new[]{"rd-downloads","rd-torrents","tb-webdl","tb-torrents"}[_settingsFocus],0);return;}
                if(_settingsFocus==2){OpenUsbInstall();return;}
                if(_settingsFocus==1){_cloudMenu=true;_settingsFocus=0;return;}
                StartPairSession(true); _uiOverlay = UiOverlay.QrPair;
            }
            else if(_cloudFiles.Count>0)
            {var file=_cloudFiles[_settingsFocus];if(!file.Ready){_cloudMessage=file.Detail;return;}if(file.Folder)LoadCloud(file.Locator,0);else try{QueuePersonalLink(file.Name,file.Locator,true);}catch{_cloudMessage="Could not queue this cloud file";}}
        }
        void DrawMyFiles(IntPtr r,SDL_Rect sheet)
        {
            if (_usbInstallOpen) { DrawUsbInstall(r, sheet); return; }
            TextPx(r,sheet.x,sheet.y+15,28,"Downloads",White);
            TextFit(r,sheet.x,sheet.y+61,18,sheet.w,"Queue your own files through the existing download and install pipeline.",Muted);
            if(_cloudFolder=="")
            {string[] names=_cloudMenu?new[]{"Real-Debrid files","Real-Debrid torrents","TorBox files","TorBox torrents"}:new[]{"Paste a download link","Browse stored debrid files","Install from USB"};for(int i=0;i<names.Length;i++)DrawSettingsRow(r,sheet.x,sheet.y+112+i*96,sheet.w,84,i,names[i],!_cloudMenu&&i==0?"Direct PKG/archive URL or a supported hoster link":!_cloudMenu&&i==2?"Browse a connected drive and select packages or archives":"Ready files and cached torrents in your own account","Open");}
            else if(!_cloudBusy)
            {EnsureVisible(ref _cloudScroll,_settingsFocus,_cloudFiles.Count,5);for(int i=0;i<5&&_cloudScroll+i<_cloudFiles.Count;i++){int n=_cloudScroll+i;var f=_cloudFiles[n];DrawSettingsRow(r,sheet.x,sheet.y+112+i*96,sheet.w,84,n,f.Name,f.Detail,!f.Ready?"Not ready":f.Folder?"Open":"Queue");}}
            if(_cloudBusy)DrawActivityRail(r,new SDL_Rect{x=sheet.x,y=sheet.y+102,w=sheet.w,h=3});
            TextFit(r,sheet.x,sheet.y+617,18,sheet.w,_cloudMessage,Muted);
            if(_cloudFolder!="")TextFit(r,sheet.x,sheet.y+666,18,sheet.w,"LEFT/RIGHT page "+(_cloudPage+1)+"  ·  TRIANGLE refresh  ·  CIRCLE back",Dim);
        }
    }
}
