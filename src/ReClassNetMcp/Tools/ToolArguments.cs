using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Tools
{
    internal sealed class ToolArguments
    {
        private readonly JObject arguments;

        public ToolArguments(JObject arguments)
        {
            this.arguments = arguments ?? new JObject();
        }

        public bool Has(string name)
        {
            var token = arguments[name];
            return token != null && token.Type != JTokenType.Null;
        }

        public JToken Raw(string name)
        {
            var token = arguments[name];

            return token == null || token.Type == JTokenType.Null ? null : token;
        }

        public string String(string name)
        {
            var value = OptionalString(name, null);
            if (value == null)
            {
                throw Missing(name);
            }

            return value;
        }

        public string OptionalString(string name, string fallback)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type != JTokenType.String)
            {
                throw Invalid(name, "a string");
            }

            return (string)token;
        }

        public long Integer(string name)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw Missing(name);
            }

            return ReadInteger(name, token);
        }

        public long OptionalInteger(string name, long fallback)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            return ReadInteger(name, token);
        }

        public int Count(string name, int fallback, int maximum)
        {
            var value = OptionalInteger(name, fallback);
            if (value < 0)
            {
                throw new InvalidArgumentsException($"'{name}' must not be negative");
            }

            if (value > maximum)
            {
                throw new InvalidArgumentsException($"'{name}' must not exceed {maximum}");
            }

            return (int)value;
        }

        public bool Bool(string name, bool fallback)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type != JTokenType.Boolean)
            {
                throw Invalid(name, "a boolean");
            }

            return (bool)token;
        }

        public Guid Uuid(string name)
        {
            var value = String(name);
            if (!Guid.TryParse(value, out var uuid))
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid uuid: {value}");
            }

            return uuid;
        }

        public IntPtr Address(string name)
        {
            return ParseAddress(name, String(name));
        }

        public byte[] Data()
        {
            var hex = OptionalString("hex", null);
            if (hex != null)
            {
                return DecodeHex("hex", hex);
            }

            var base64 = OptionalString("base64", null);
            if (base64 != null)
            {
                return DecodeBase64("base64", base64);
            }

            throw new InvalidArgumentsException("Provide the payload as either 'hex' or 'base64'");
        }

        //
        // A single object where an array belongs is taken as a batch of one, and Strings() does
        // the same for a bare string. Models write the scalar form constantly, and refusing it
        // buys a round trip that teaches the caller nothing.
        //
        public IReadOnlyList<JObject> Objects(string name)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw Missing(name);
            }

            var items = new List<JObject>();

            if (token is JObject single)
            {
                items.Add(single);
                return items;
            }

            if (!(token is JArray array))
            {
                throw Invalid(name, "an object or an array of objects");
            }

            foreach (var entry in array)
            {
                if (!(entry is JObject item))
                {
                    throw Invalid(name, "an array of objects");
                }

                items.Add(item);
            }

            return items;
        }

        public IReadOnlyList<string> Strings(string name)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return new string[0];
            }

            if (token.Type == JTokenType.String)
            {
                return new[] { (string)token };
            }

            if (!(token is JArray array))
            {
                throw Invalid(name, "a string or an array of strings");
            }

            var items = new List<string>(array.Count);
            foreach (var entry in array)
            {
                if (entry.Type != JTokenType.String)
                {
                    throw Invalid(name, "an array of strings");
                }

                items.Add((string)entry);
            }

            return items;
        }

        //
        // Addresses are hexadecimal with or without the 0x, so a bare 10 is 0x10, exactly as in
        // an address formula. Sixteen digits is as wide as a 64 bit pointer gets, and on a 32 bit
        // host anything above uint.MaxValue is refused rather than truncated into an address that
        // looks plausible.
        //
        public static IntPtr ParseAddress(string name, string value)
        {
            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }

            if (text.Length == 0 || text.Length > 16)
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid hexadecimal address: {value}");
            }

            if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid hexadecimal address: {value}");
            }

            if (IntPtr.Size == 4 && parsed > uint.MaxValue)
            {
                throw new InvalidArgumentsException($"'{name}' does not fit into a 32 bit address: {value}");
            }

            return unchecked((IntPtr)(long)parsed);
        }

        //
        // Spaces and dashes go before parsing, so "48 8B 05" and "48-8B-05" both decode. That is
        // the shape a byte string gets pasted in as, and refusing it would only ask the caller to
        // reformat something that was already right.
        //
        public static byte[] DecodeHex(string name, string value)
        {
            var text = value.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }

            if (text.Length == 0 || text.Length % 2 != 0)
            {
                throw new InvalidArgumentsException($"'{name}' must be an even length hexadecimal string");
            }

            var data = new byte[text.Length / 2];
            for (var i = 0; i < data.Length; ++i)
            {
                if (!byte.TryParse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]))
                {
                    throw new InvalidArgumentsException($"'{name}' contains a non hexadecimal character at offset {i * 2}");
                }
            }

            return data;
        }

        public static byte[] DecodeBase64(string name, string value)
        {
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new InvalidArgumentsException($"'{name}' is not valid base64");
            }
        }

        //
        // An integer sent as a JSON string is accepted, because that is what models send. Decimal
        // here, unlike ParseAddress: the wire format of a count is not the wire format of an
        // address.
        //
        private static long ReadInteger(string name, JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                return (long)token;
            }

            if (token.Type == JTokenType.String && long.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw Invalid(name, "an integer");
        }

        private static InvalidArgumentsException Missing(string name)
        {
            return new InvalidArgumentsException($"Missing required argument '{name}'");
        }

        private static InvalidArgumentsException Invalid(string name, string expected)
        {
            return new InvalidArgumentsException($"'{name}' must be {expected}");
        }
    }
}
