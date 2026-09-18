using System;

namespace Orbis
{
    /// <summary>
    /// Pure scheduling decisions for the single transfer worker. This file has no
    /// dependency on DownloadManager state or on the SDL host, so the host
    /// regression compiles it as-is and exercises the policy that ships.
    /// </summary>
    internal static class QueueScheduler
    {
        /// <summary>
        /// Whether a queue row may claim the one active transfer slot. A parked
        /// TorBox preparation is never claimable: the parked poller owns it until
        /// the provider publishes the file, so no second resolve is ever submitted
        /// for the same web download.
        /// </summary>
        internal static bool CanClaim(bool parked, bool queued, bool background,
            bool backgroundBusy, bool residentBusy, bool publishOnly,
            bool localPending, bool localExists, bool dependencyAllowed,
            long nowTicks, long retryAfterTicks, long transferClockMs,
            long nextJobStartAt, int activeCount, int workerCount, bool pathActive)
        {
            if (!queued || background || parked) return false;
            if (backgroundBusy) return false;
            if (activeCount >= workerCount) return false;
            if (transferClockMs < nextJobStartAt) return false;
            if (residentBusy && !publishOnly) return false;
            if (localPending && !localExists && !publishOnly) return false;
            if (!dependencyAllowed) return false;
            if (retryAfterTicks > nowTicks) return false;
            if (pathActive) return false;
            return true;
        }

        /// <summary>
        /// A parked row is polled only while it is still the same queued row,
        /// unsolicited by pause/cancel/remove, bound to the same attempt, with a
        /// durable provider record to poll, and its backoff has elapsed.
        /// </summary>
        internal static bool CanPollParked(bool parked, bool queued, bool paused,
            bool cancelRequested, bool removeRequested, bool sameReference,
            bool attemptMatches, bool hasPendingEntry, long dueTicks, long nowTicks)
        {
            if (!parked || !queued || paused || cancelRequested || removeRequested) return false;
            if (!sameReference || !attemptMatches) return false;
            if (!hasPendingEntry) return false;
            return nowTicks >= dueTicks;
        }

        /// <summary>
        /// Provider list cadence, matching TorBoxClient's schedule: a fast first
        /// check for a freshly cached file, then the API's five second cadence.
        /// Bounded from above so a parked row can never busy-loop.
        /// </summary>
        internal static int NextPollDelayMs(int completedPolls)
        {
            if (completedPolls <= 0) return 1000;
            return completedPolls == 1 ? 2000 : 5000;
        }

        /// <summary>
        /// Completed fraction for one weighted progress segment. Unknown totals
        /// fall back to the row's own terminal state instead of inventing a size.
        /// </summary>
        internal static double SegmentFill(bool completed, long done, long total)
        {
            if (total > 0)
            {
                if (done < 0) done = 0;
                double fill = done / (double)total;
                return fill > 1 ? 1 : fill;
            }
            return completed ? 1 : 0;
        }

        /// <summary>
        /// Segment width weight. Real sizes win; a partially transferred row with
        /// no known total uses its durable byte count; anything else is an equal,
        /// safe fallback so no segment ever collapses to zero width.
        /// </summary>
        internal static long SegmentWeight(long done, long total)
        {
            if (total > 0) return total;
            if (done > 0) return done;
            return 1;
        }

        internal static int Percent(long done, long total)
        {
            if (total <= 0 || done <= 0) return 0;
            long percent = done * 100 / total;
            return percent > 100 ? 100 : (int)percent;
        }
    }
}
