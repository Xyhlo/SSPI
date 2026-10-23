using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal enum DlState
    {
        Queued,
        Resolving,
        Downloading,
        Finalizing,
        Paused,
        Completed,
        Failed,
        Canceled,
        Installing,
        Submitted,
        Installed
    }
}
