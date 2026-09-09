using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orbis
{
    internal static class Util
    {
        public static void PrepareAsemblies()
        {
            bool NewAssembly = false; 
            var AllMethods = BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            List<Assembly> Ready = new List<Assembly>();

            while (NewAssembly)
            {
                foreach (var Asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (Ready.Contains(Asm))
                        continue;

                    NewAssembly = true;
                    Ready.Add(Asm);
                    foreach (var type in Asm.GetTypes())
                    {
                        foreach (var method in type.GetMethods(AllMethods))
                        {
                            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        }
                    }
                }
            }
        }
    }
}
