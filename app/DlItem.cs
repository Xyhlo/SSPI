using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class DlItem
    {
        public string Id;
        public string TitleId;
        public string Name;
        public string Kind;
        public string Label;
        public string HosterUrl;
        /// <summary>Package Source provenance snapshot. Empty values identify a legacy queue row.</summary>
        public string SourceId = "";
        public string SourceVersion = "";
        public string PackageVersion = "";
        public string CandidateId = "";
        /// <summary>Direct, HosterLanding, or Unknown. Empty is a legacy Unknown value.</summary>
        public string AccessType = "";
        public string MirrorCandidates = "";
        public string FanOutPendingPaths = "";
        public string ArchiveVolumes = "";
        public string ArchivePassword = "";
        public string ArchivePasswords = "";
        public string ContainerFormat = "";
        public bool LocalSource;
        public string LocalSourceFingerprint = "";
        public string InstallAfterId = "";
        public bool InstallAfterConfirmed;
        public bool BgftLocalInstall;
        // Persisted as resident_archive for compatibility; V4 PKGs also let the
        // resident own installation through to BGFT acknowledgement.
        public bool ResidentArchive;
        public bool ResidentStaged;
        public bool ResidentRemovePending;
        // Transient (not saved): when this removal started, and since when no worker heartbeat was seen.
        public long ResidentRemoveStarted, ResidentRemoveSilentSince;
        // Remove anyway (saved): the user chose to stop tracking a removal the background worker
        // could not confirm. The worker releases it without stopping the PS4 task; files are kept.
        public bool ResidentDetachRequested;
        // Transient: when Remove anyway was sent, and whether this removal now waits for that choice.
        internal long ResidentDetachSentAt;
        internal bool ResidentRemoveStuck;
        public bool RemoveRequested;
        public bool ResidentAutoInstall;
        public string ResidentGeneration;
        public string ResolvedProviderId = "";
        internal bool ForegroundTransfer;
        internal string BackgroundWaitLoggedReason;
        // Silent-feeding watch (not saved): bytes seen and when they last changed.
        internal long FeedWatchBytes, FeedWatchTicks;
        public bool InstallOrderReady;
        public string ExpectedSha256 = "";
        public long ExpectedByteSize;
        public string ExpectedContentId = "";
        public string SourcePageUrl = "";
        public string ExpiresUtc = "";
        public string SourceAttribution = "";
        public string ImageUrl;
        public string DestPath;
        public DlState State;
        public long Done;
        public long Total;
        public double BytesPerSec;
        public int EtaSeconds;
        internal long StatsAt, StatsDone, StatsTotal, StatsAdvancedAt, StatsObservedDone;
        internal bool StatsInitialized;
        internal LiveTransferMeter LiveMeter;
        internal double[] RateSamples;
        internal long BgftRejectedCompletionTotal;
        internal string StatsPhase;
        internal int StatsAttempt;
        public string Error;
        public string StatusText;
        /// <summary>CDN hostname only (no path/query).</summary>
        public string CdnHost;
        public string TransferMode;
        public bool InstallConfirmed;
        // Accepted installation without package-level completion proof. Cancellation
        // may stop queue tracking, but must not delete input still held by the PS4.
        public bool InstallSubmitted;
        internal long SubmittedWaitStartedMs = -1;
        internal bool SubmittedNeedsAttention;
        public bool Background;
        public bool BgftLoopback;
        public bool BgftResident;
        public bool BgftDirect;
        public bool BgftLoopbackServed;
        public int BgftTaskId = -1;
        public string BgftContentId;
        public int BgftSubType;
        public int BgftPollFailures;
        public int BgftFeederMisses;
        /// <summary>Preflight Content-Range total for web BGFT (must match transfer).</summary>
        public long BgftExpectedSize;
        public bool BgftTitlePresenceKnown;
        public bool BgftTitlePresentAtStart;
        public bool BgftDownloadPhaseConfirmed;
        // Transient (not persisted) — consecutive poll evidence for web BGFT.
        public int BgftDownloadCompletePolls;
        public int BgftCopyCompletePolls;
        public int BgftTitlePresentPolls;
        public int BgftInstalledProofPolls;
        /// <summary>Done bytes last time progress advanced (stall detection).</summary>
        public long BgftLastProgressDone;
        public int BgftStallPolls;
        /// <summary>Skip system BGFT for this attempt (after stall/fail → in-app).</summary>
        public bool ForceLocalInstall;
        // Set once after AppInstUtil synchronously rejects an add-on (ADDCONT_BROKEN);
        // the next install attempt registers the retained PKG through loopback BGFT.
        public bool AddOnBgftFallback;
        // Saved once the PS4 refused the storage route for this base without creating
        // a task; its later install attempts use loopback BGFT.
        public bool BaseStorageRefused;
        public long RetryAfterUtcTicks;
        public int TransientHttpRetries;
        internal bool HttpRetryRenewPending;
        public long HttpRetryLastDurableBytes;
        // Host-support check retries have their own budget, separate from HTTP retries.
        public int HostSupportRetries;
        public long HostSupportRetryDurableBytes;
        public int InstallRetries;
        // Reuse a resolved URL for transient failures; never persist it in diagnostics.
        public string HttpRetryUrl;
        public int ResidentLinkRenewals;
        public long ResidentLastRenewalBytes;
        public bool ResidentRetryPending;
        /// <summary>Incremented each transfer claim; Refresh must not stomp a newer attempt.</summary>
        public int AttemptId;
        public bool PauseRequested;
        // Last accepted resident command wins over a status sampled before it.
        public bool? ResidentPauseDesired;
        public long ResidentPauseRevision;
        public bool CancelRequested;

        // Provider preparation parking. A provider-side prepare must not own the one
        // transfer slot, so the row stays Queued with its provider progress while
        // the worker advances other games. Transient by design: a restart rebuilds
        // it from the account-bound durable pending record. Queue ownership and
        // poll cadence are persisted; signed URLs and provider credentials are not.
        internal bool ParkedForProvider;
        internal long ParkPollDueUtcTicks;
        internal int ParkPollCount;
        internal int ParkTransientFailures;
        internal string ParkProviderId = "";
        internal string ParkHostUrl = "";
        // URLs whose provider create/pending receipt was durably observed for this
        // parked operation. A missing receipt after restart is an action state,
        // never permission to silently create the remote operation again.
        internal string ParkStartedUrls = "";
        internal bool ParkRetryWithoutReceipt;
        internal string ParkRejectedProviderIds = "";
        // Archive providers are bound to each immutable source URL, not to the
        // job-wide provider selected for a different volume.
        internal string ArchiveProviderState = "";
        internal string ParkLastState = "";
        internal long ParkStartedUtcTicks;
        // Transient: last time this session saw the parked provider report progress.
        internal long ParkProgressUtcTicks;
        internal int ProviderTransientRetries;
        // Fresh same-link attempts after a provider reported the host download failed.
        internal int ProviderHostRetries;
        internal int TorBoxThrottleNoProgress;
        internal int TorBoxThrottleRenewalsWithoutProgress;
        internal long TorBoxThrottleDurableBytes;
        internal bool TorBoxThrottleRenewPending;
        internal int ArchiveRetryVolume = -1;
        internal string ArchiveRetryUrl = "";
    }
}
