using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Orbis
{
    public unsafe class User
    {
        // Routine acknowledgements are latest-wins footer status. Animated cards are
        // opt-in for meaningful long-running/terminal events.
        static readonly object NotificationLock = new object();
        static readonly Queue<string> ToastQueue = new Queue<string>();
        static string PendingStatus;
        static string LastStatus;
        static uint LastStatusAt;
        static string LastToast;
        static uint LastToastAt;

        static uint Tick() { return unchecked((uint)Environment.TickCount); }
        static uint Elapsed(uint since) { return unchecked(Tick() - since); }

        public static void Notify(string Message)
        {
            if (string.IsNullOrWhiteSpace(Message)) return;
            string message = Message.Trim();
            lock (NotificationLock)
            {
                if (string.Equals(message, LastStatus, StringComparison.Ordinal) &&
                    Elapsed(LastStatusAt) < 750) return;
                LastStatus = message;
                LastStatusAt = Tick();
                PendingStatus = message;
            }
        }

        public static void Notify(string Icon, string Message) { Notify(Message); }

        public static void NotifyToast(string Message)
        {
            if (string.IsNullOrWhiteSpace(Message)) return;
            string message = Message.Trim();
            lock (NotificationLock)
            {
                if ((string.Equals(message, LastToast, StringComparison.Ordinal) &&
                     Elapsed(LastToastAt) < 4000) || ToastQueue.Contains(message)) return;
                LastToast = message;
                LastToastAt = Tick();
                while (ToastQueue.Count >= 3) ToastQueue.Dequeue();
                ToastQueue.Enqueue(message);
            }
        }

        public static bool TryTakeStatus(out string message)
        {
            lock (NotificationLock)
            {
                message = PendingStatus;
                PendingStatus = null;
                return !string.IsNullOrEmpty(message);
            }
        }

        public static bool TryTakeToast(out string message)
        {
            lock (NotificationLock)
            {
                if (ToastQueue.Count == 0)
                {
                    message = null;
                    return false;
                }
                message = ToastQueue.Dequeue();
                return true;
            }
        }

        //Notify Method By OSM-Made, https://github.com/OSM-Made/PS4-Notify/blob/main/Notify.cpp

        #region Constants
        public const string PlaystationButtons = "cxml://psnotification/tex_icon_system";
        public const string CircleWithSlash = "cxml://psnotification/tex_icon_ban";
        public const string IInChatBubble = "cxml://psnotification/tex_default_icon_notification";
        public const string DefaultIconMessage = "cxml://psnotification/tex_default_icon_message";
        public const string DefaultIconFriend = "cxml://psnotification/tex_default_icon_friend";
        public const string DefaultIconTrophy = "cxml://psnotification/tex_default_icon_trophy";
        public const string DefaultIconDownload = "cxml://psnotification/tex_default_icon_download";
        public const string DefaultIconUpload16_9 = "cxml://psnotification/tex_default_icon_upload_16_9";
        public const string DefaultIconCloudClient = "cxml://psnotification/tex_default_icon_cloud_client";
        public const string DefaultIconActivity = "cxml://psnotification/tex_default_icon_activity";
        public const string DefaultIconSmaps = "cxml://psnotification/tex_default_icon_smaps";
        public const string DefaultIconShareplay = "cxml://psnotification/tex_default_icon_shareplay";
        public const string DefaultIconTips = "cxml://psnotification/tex_default_icon_tips";
        public const string DefaultIconEvents = "cxml://psnotification/tex_default_icon_events";
        public const string DefaultIconShareScreen = "cxml://psnotification/tex_default_icon_share_screen";
        public const string DefaultIconCommunity = "cxml://psnotification/tex_default_icon_community";
        public const string DefaultIconLfps = "cxml://psnotification/tex_default_icon_lfps";
        public const string DefaultIconTournament = "cxml://psnotification/tex_default_icon_tournament";
        public const string DefaultIconTeam = "cxml://psnotification/tex_default_icon_team";
        public const string DefaultAvatar = "cxml://psnotification/tex_default_avatar";
        public const string IconCapture = "cxml://psnotification/tex_icon_capture";
        public const string IconStartRec = "cxml://psnotification/tex_icon_start_rec";
        public const string IconStopRec = "cxml://psnotification/tex_icon_stop_rec";
        public const string IconLiveProhibited = "cxml://psnotification/tex_icon_live_prohibited";
        public const string IconLiveStart = "cxml://psnotification/tex_icon_live_start";
        public const string IconLoading = "cxml://psnotification/tex_icon_loading";
        public const string IconLoading16_9 = "cxml://psnotification/tex_icon_loading_16_9";
        public const string IconCountdown = "cxml://psnotification/tex_icon_countdown";
        public const string IconParty = "cxml://psnotification/tex_icon_party";
        public const string IconShareplay = "cxml://psnotification/tex_icon_shareplay";
        public const string IconBroadcast = "cxml://psnotification/tex_icon_broadcast";
        public const string IconPsnowToast = "cxml://psnotification/tex_icon_psnow_toast";
        public const string AudioDeviceHeadphone = "cxml://psnotification/tex_audio_device_headphone";
        public const string AudioDeviceHeadset = "cxml://psnotification/tex_audio_device_headset";
        public const string AudioDeviceMic = "cxml://psnotification/tex_audio_device_mic";
        public const string AudioDeviceMorpheus = "cxml://psnotification/tex_audio_device_morpheus";
        public const string DeviceBattery0 = "cxml://psnotification/tex_device_battery_0";
        public const string DeviceBattery1 = "cxml://psnotification/tex_device_battery_1";
        public const string DeviceBattery2 = "cxml://psnotification/tex_device_battery_2";
        public const string DeviceBattery3 = "cxml://psnotification/tex_device_battery_3";
        public const string DeviceBatteryNocharge = "cxml://psnotification/tex_device_battery_nocharge";
        public const string DeviceCompApp = "cxml://psnotification/tex_device_comp_app";
        public const string DeviceController = "cxml://psnotification/tex_device_controller";
        public const string DeviceJediUsb = "cxml://psnotification/tex_device_jedi_usb";
        public const string DeviceBlaster = "cxml://psnotification/tex_device_blaster";
        public const string DeviceHeadphone = "cxml://psnotification/tex_device_headphone";
        public const string DeviceHeadset = "cxml://psnotification/tex_device_headset";
        public const string DeviceKeyboard = "cxml://psnotification/tex_device_keyboard";
        public const string DeviceMic = "cxml://psnotification/tex_device_mic";
        public const string DeviceMorpheus = "cxml://psnotification/tex_device_morpheus";
        public const string DeviceMouse = "cxml://psnotification/tex_device_mouse";
        public const string DeviceMove = "cxml://psnotification/tex_device_move";
        public const string DeviceRemote = "cxml://psnotification/tex_device_remote";
        public const string DeviceOmit = "cxml://psnotification/tex_device_omit";
        public const string IconConnect = "cxml://psnotification/tex_icon_connect";
        public const string IconEventToast = "cxml://psnotification/tex_icon_event_toast";
        public const string MorpheusTrophyBronze = "cxml://psnotification/tex_morpheus_trophy_bronze";
        public const string MorpheusTrophyGold = "cxml://psnotification/tex_morpheus_trophy_gold";
        public const string MorpheusTrophyPlatinum = "cxml://psnotification/tex_morpheus_trophy_platinum";
        public const string IconChampionsLeague = "cxml://psnotification/tex_icon_champions_league";
        #endregion

        #region NotifyTypes
        enum NotifyType
        {
            NotificationRequest = 0,
            SystemNotification = 1,
            SystemNotificationWithUserId = 2,
            SystemNotificationWithDeviceId = 3,
            SystemNotificationWithDeviceIdRelatedToUser = 4,
            SystemNotificationWithText = 5,
            SystemNotificationWithTextRelatedToUser = 6,
            SystemNotificationWithErrorCode = 7,
            SystemNotificationWithAppId = 8,
            SystemNotificationWithAppName = 9,
            SystemNotificationWithAppInfo = 9,
            SystemNotificationWithAppNameRelatedToUser = 10,
            SystemNotificationWithParams = 11,
            SendSystemNotificationWithUserName = 12,
            SystemNotificationWithUserNameInfo = 13,
            SendAddressingSystemNotification = 14,
            AddressingSystemNotificationWithDeviceId = 15,
            AddressingSystemNotificationWithUserName = 16,
            AddressingSystemNotificationWithUserId = 17,
            UNK_1 = 100,
            TrcCheckNotificationRequest = 101,
            NpDebugNotificationRequest = 102,
            UNK_2 = 102
        }


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Size = 0xC30)]
        struct NotifyBuffer
        {
            public NotifyType Type;
            public int ReqId;
            public int Priority;
            public int MsgId;
            public int TargetId;
            public int UserId;
            public int unk1;
            public int unk2;
            public int AppId;
            public int ErrorNum;
            public int unk3;
            public byte UseIconImageUri;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
            public string Message;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
            public string Uri;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
            public string unkstr;


            public NotifyBuffer()
            {
                Type = NotifyType.NotificationRequest;
                UseIconImageUri = 1;
                TargetId = -1;
                unk3 = 0;
            }
        }

        #endregion

        [DllImport("libkernel")]
        static extern void sceKernelSendNotificationRequest(long unk1, IntPtr Buffer, long size, long unk2);
    }
}
