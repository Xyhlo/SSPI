using System;
using System.IO;
using System.Text;

namespace Orbis
{
    internal static class ResidentSfo
    {
        const uint Magic = 0x46535000;
        const ushort FmtUtf8 = 0x0004;
        const ushort FmtUtf8Special = 0x0204;

        public static bool TrySetUtf8(string path, string key, string value, out string error)
        {
            error = null;
            byte[] data;
            int index;
            int valueOffset;
            int paramMax;
            if (!TryFindUtf8(path, key, out data, out index, out valueOffset, out paramMax,
                out error)) return false;
            byte[] valueBytes = Encoding.UTF8.GetBytes(value ?? "");
            if (valueBytes.Length + 1 > paramMax)
            {
                error = "SFO key " + key + " does not fit";
                return false;
            }
            Array.Clear(data, valueOffset, paramMax);
            Buffer.BlockCopy(valueBytes, 0, data, valueOffset, valueBytes.Length);
            WriteInt32(data, index + 4, valueBytes.Length + 1);
            try
            {
                File.WriteAllBytes(path, data);
                return true;
            }
            catch (Exception ex)
            {
                error = "SFO write failed: " + ex.Message;
                return false;
            }
        }

        public static bool TryGetUtf8(string path, string key, out string value, out string error)
        {
            value = null;
            byte[] data;
            int index;
            int valueOffset;
            int paramMax;
            if (!TryFindUtf8(path, key, out data, out index, out valueOffset, out paramMax,
                out error)) return false;
            int paramLen = BitConverter.ToInt32(data, index + 4);
            if (paramLen <= 0 || paramLen > paramMax) paramLen = paramMax;
            int n = 0;
            while (n < paramLen && n < paramMax && data[valueOffset + n] != 0) n++;
            value = Encoding.UTF8.GetString(data, valueOffset, n);
            return true;
        }

        static bool TryFindUtf8(string path, string key, out byte[] data, out int index,
            out int valueOffset, out int paramMax, out string error)
        {
            data = null;
            index = 0;
            valueOffset = 0;
            paramMax = 0;
            error = null;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(key))
            {
                error = "SFO patch metadata is invalid";
                return false;
            }
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                error = "SFO read failed: " + ex.Message;
                return false;
            }
            if (data.Length < 20)
            {
                error = "SFO is too small";
                return false;
            }
            if (BitConverter.ToUInt32(data, 0) != Magic)
            {
                error = "SFO magic is invalid";
                return false;
            }
            int keyTable = BitConverter.ToInt32(data, 8);
            int dataTable = BitConverter.ToInt32(data, 12);
            int count = BitConverter.ToInt32(data, 16);
            if (keyTable < 20 || dataTable < keyTable || count < 1 || count > 256)
            {
                error = "SFO header is invalid";
                return false;
            }
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            for (int i = 0; i < count; i++)
            {
                int at = 20 + i * 16;
                if (at + 16 > data.Length)
                {
                    error = "SFO index is truncated";
                    return false;
                }
                int keyOffset = keyTable + BitConverter.ToUInt16(data, at);
                ushort fmt = BitConverter.ToUInt16(data, at + 2);
                paramMax = BitConverter.ToInt32(data, at + 8);
                valueOffset = dataTable + BitConverter.ToInt32(data, at + 12);
                if (keyOffset < 0 || keyOffset >= data.Length) continue;
                if (!KeyEquals(data, keyOffset, keyBytes)) continue;
                if (fmt != FmtUtf8 && fmt != FmtUtf8Special)
                {
                    error = "SFO key " + key + " is not UTF-8";
                    return false;
                }
                if (paramMax <= 0 || valueOffset < 0 || valueOffset + paramMax > data.Length)
                {
                    error = "SFO value is truncated";
                    return false;
                }
                index = at;
                return true;
            }
            error = "SFO key " + key + " is missing";
            return false;
        }

        static bool KeyEquals(byte[] data, int offset, byte[] key)
        {
            if (offset + key.Length >= data.Length) return false;
            for (int i = 0; i < key.Length; i++)
                if (data[offset + i] != key[i]) return false;
            return data[offset + key.Length] == 0;
        }

        static void WriteInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }
}
