using System;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Tools
{
    internal sealed class ToolResult
    {
        public JObject Structured { get; }

        public string Text { get; }

        public bool IsError { get; }

        private ToolResult(JObject structured, string text, bool isError)
        {
            Structured = structured;
            Text = text;
            IsError = isError;
        }

        public static ToolResult Ok(JObject structured)
        {
            return new ToolResult(structured, null, false);
        }

        public static ToolResult Ok(JObject structured, string text)
        {
            return new ToolResult(structured, text, false);
        }

        public static ToolResult Failure(string message, string hint)
        {
            var structured = new JObject { ["error"] = message };
            if (!string.IsNullOrEmpty(hint))
            {
                structured["hint"] = hint;
            }

            var text = string.IsNullOrEmpty(hint) ? message : message + "\n" + hint;
            return new ToolResult(structured, text, true);
        }
    }
}
