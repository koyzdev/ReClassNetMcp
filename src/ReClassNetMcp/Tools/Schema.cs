using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Tools
{
    internal sealed class Field
    {
        public string Name { get; }

        public JObject Type { get; }

        public bool Required { get; }

        public Field(string name, JObject type, bool required)
        {
            Name = name;
            Type = type;
            Required = required;
        }
    }

    internal static class Schema
    {
        public static JObject Object(params Field[] fields)
        {
            return Object(false, fields);
        }

        public static JObject Object(bool allowAdditional, params Field[] fields)
        {
            var properties = new JObject();
            var required = new JArray();

            foreach (var field in fields)
            {
                properties[field.Name] = field.Type;

                if (field.Required)
                {
                    required.Add(field.Name);
                }
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = allowAdditional
            };

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        public static Field Required(string name, JObject type, string description)
        {
            return new Field(name, Describe(type, description), true);
        }

        public static Field Optional(string name, JObject type, string description)
        {
            return new Field(name, Describe(type, description), false);
        }

        public static JObject Text()
        {
            return new JObject { ["type"] = "string" };
        }

        public static JObject Text(int minLength, int maxLength)
        {
            return new JObject { ["type"] = "string", ["minLength"] = minLength, ["maxLength"] = maxLength };
        }

        public static JObject Bool()
        {
            return new JObject { ["type"] = "boolean" };
        }

        public static JObject Integer()
        {
            return new JObject { ["type"] = "integer" };
        }

        public static JObject Integer(long minimum, long maximum)
        {
            return new JObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
        }

        public static JObject Number()
        {
            return new JObject { ["type"] = "number" };
        }

        public static JObject Address()
        {
            return new JObject
            {
                ["type"] = "string",
                ["pattern"] = "^(0[xX])?[0-9a-fA-F]+$"
            };
        }

        public static JObject Formula()
        {
            return new JObject { ["type"] = "string", ["minLength"] = 1 };
        }

        public static JObject Uuid()
        {
            return new JObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
            };
        }

        public static JObject Enum(params string[] values)
        {
            var array = new JArray();
            foreach (var value in values)
            {
                array.Add(value);
            }

            return new JObject { ["type"] = "string", ["enum"] = array };
        }

        public static JObject ArrayOf(JObject items)
        {
            return new JObject { ["type"] = "array", ["items"] = items };
        }

        public static JObject ArrayOf(JObject items, int maxItems)
        {
            return new JObject { ["type"] = "array", ["items"] = items, ["maxItems"] = maxItems };
        }

        public static JObject Map(JObject values)
        {
            return new JObject { ["type"] = "object", ["additionalProperties"] = values };
        }

        public static JObject AnyObject()
        {
            return new JObject { ["type"] = "object", ["additionalProperties"] = true };
        }

        private static JObject Describe(JObject type, string description)
        {
            var copy = (JObject)type.DeepClone();
            if (!string.IsNullOrEmpty(description))
            {
                copy["description"] = description;
            }

            return copy;
        }
    }
}
