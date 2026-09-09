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
    }
}