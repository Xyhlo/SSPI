using System;
using System.Runtime.CompilerServices;
using Orbis.String;

namespace Orbis.Internals
{
    public class IO
    {

        public static string GetAppBaseDirectory()
        {
            return (CString)GetBaseDirectory();
        }
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        static extern IntPtr GetBaseDirectory();

        /// <summary>The launcher's record of this start, one "key=value" per line.</summary>
        public static string GetLaunchRecord()
        {
            return (CString)GetLaunchInfo();
        }

        [MethodImpl(MethodImplOptions.InternalCall)]
        static extern IntPtr GetLaunchInfo();
    }
}