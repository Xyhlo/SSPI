using System;

namespace Orbis
{
    internal static class BgftCancellation
    {
        internal const int TaskNotFound = unchecked((int)0x80990019);
        internal delegate int FindTask(string content, int subtype, out int task);

        internal static bool Cancel(int task, string content, int subtype, FindTask find,
            Func<int, bool> owned, Func<int, int> stop, Func<int, int> unregister,
            Action<int> release, out int activeTask, out string error)
        {
            activeTask = task;
            error = null;
            if (string.IsNullOrWhiteSpace(content) || subtype <= 0)
            { error = "BGFT cancel requires content identity"; return false; }
            if (!owned(task))
            {
                int rc = find(content, subtype, out activeTask);
                if (rc == TaskNotFound || (rc == 0 && activeTask < 0))
                { release(task); activeTask = -1; return true; }
                if (rc != 0) { error = "BGFT find 0x" + unchecked((uint)rc).ToString("X8"); return false; }
                if (!owned(activeTask))
                { error = "BGFT task " + activeTask + " is not owned by SSPI; cancel refused"; return false; }
            }
            int stopRc = stop(activeTask);
            int unregisterRc = unregister(activeTask);
            if (unregisterRc != 0 && unregisterRc != TaskNotFound)
            {
                error = "BGFT cancel stop 0x" + unchecked((uint)stopRc).ToString("X8") +
                    " unregister 0x" + unchecked((uint)unregisterRc).ToString("X8");
                return false;
            }
            release(activeTask);
            return true;
        }
    }
}
