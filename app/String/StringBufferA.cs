using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orbis.String
{
    public abstract unsafe class StringBufferA : IEnumerable<byte>
    {
        public long? FixedLength = null;
        public sbyte* Address => (sbyte*)Ptr;
        public IEnumerator<byte> GetEnumerator()
        {
            int Length = 0;
            var Ptr = this.Ptr;
            while (true)
            {
                var Current = ReadNext(ref Ptr);
                if (Current == 0)
                    yield break;

                yield return Current;

                if (FixedLength != null && Length++ > FixedLength)
                    yield break;
            }
        }
        
        public StringBufferA(void* Address) => Ptr = (ulong)Address;
        public StringBufferA(byte* Address) => Ptr = (ulong)Address;

        ulong Ptr;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe byte ReadNext(ref ulong Pointer) => *(byte*)Pointer++;

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}