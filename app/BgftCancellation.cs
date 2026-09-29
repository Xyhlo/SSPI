using System;

namespace Orbis
{
    internal static class BgftCancellation
    {
        internal const int TaskNotFound = unchecked((int)0x80990019);
        const string NotOwned = " is not owned by SSPI; operation refused";

        /// <summary>True when an operation was refused because the PS4 task belongs to
        /// something other than SSPI (it is not in the ownership journal).</summary>
        internal static bool IsForeignRefusal(string error)
        {
            return error != null && error.StartsWith("BGFT task ", StringComparison.Ordinal) &&
                error.EndsWith(NotOwned, StringComparison.Ordinal);
        }
        internal delegate int FindTask(string content, int subtype, out int task);

        internal static bool ResolveOwned(string content, int subtype, FindTask find,
            Func<int, bool> owned, out int activeTask, out string error)
        {
            activeTask = -1;
            error = null;
            if (string.IsNullOrWhiteSpace(content) || subtype <= 0)
            { error = "BGFT lookup requires content identity"; return false; }
            // Persisted task numbers can be recycled after a service/console restart.
            // Resolve the current content+subtype before reading or controlling a task.
            int rc = find(content, subtype, out activeTask);
            if (rc == TaskNotFound || (rc == 0 && activeTask < 0))
            { activeTask = -1; return true; }
            if (rc != 0)
            { activeTask = -1; error = "BGFT find 0x" + unchecked((uint)rc).ToString("X8"); return false; }
            if (!owned(activeTask))
            { error = "BGFT task " + activeTask + NotOwned; return false; }
            return true;
        }

        internal static bool Cancel(int task, string content, int subtype, FindTask find,
            Func<int, bool> owned, Func<int, int> stop, Func<int, int> unregister,
            Action<int> release, out int activeTask, out string error)
        {
            activeTask = task;
            error = null;
            if (!ResolveOwned(content, subtype, find, owned, out activeTask, out error)) return false;
            if (activeTask < 0) { release(task); return true; }
            int stopRc = stop(activeTask);
            int unregisterRc = unregister(activeTask);
            if (unregisterRc != 0 && unregisterRc != TaskNotFound)
            {
                error = "BGFT cancel stop 0x" + unchecked((uint)stopRc).ToString("X8") +
                    " unregister 0x" + unchecked((uint)unregisterRc).ToString("X8");
                return false;
            }
            release(activeTask);
            if (task != activeTask) release(task);
            return true;
        }
    }
}
