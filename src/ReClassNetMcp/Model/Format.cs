using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Model
{
    internal static class Format
    {
        public static string Hex(IntPtr value)
        {
            return Hex(value.ToInt64());
        }

        public static string Hex(long value)
        {
            return "0x" + unchecked((ulong)value).ToString("x", CultureInfo.InvariantCulture);
        }

        public static string HexBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            var characters = new char[data.Length * 2];

            for (var i = 0; i < data.Length; ++i)
            {
                var value = data[i];
                characters[i * 2] = Nibble(value >> 4);
                characters[i * 2 + 1] = Nibble(value & 0xF);
            }

            return new string(characters);
        }

        public static JObject Payload(IntPtr address, byte[] data)
        {
            return new JObject
            {
                ["address"] = Hex(address),
                ["size"] = data.Length,
                ["hex"] = HexBytes(data),
                ["base64"] = Convert.ToBase64String(data)
            };
        }

        public static JObject Page(JArray items, int offset, int limit, int total)
        {
            return new JObject
            {
                ["items"] = items,
                ["offset"] = offset,
                ["limit"] = limit,
                ["count"] = items.Count,
                ["total"] = total,
                ["hasMore"] = offset + items.Count < total
            };
        }

        private static char Nibble(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }
    }
}
