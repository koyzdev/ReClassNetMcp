using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReClassNetMcp.Configuration;

namespace ReClassNetMcp.Install
{
    internal sealed class TomlWriteOutcome
    {
        public bool Created { get; set; }

        public bool Replaced { get; set; }

        public bool Changed { get; set; }
    }

    internal static class TomlConfigWriter
    {
        public static TomlWriteOutcome Write(string path, string serverName, string url, string token)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("The configuration path is required.", nameof(path));
            }

            if (string.IsNullOrEmpty(serverName))
            {
                throw new ArgumentException("The server name is required.", nameof(serverName));
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("The server url is required.", nameof(url));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var exists = File.Exists(path);
            var original = exists ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var newLine = original.IndexOf('\n') < 0 || original.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

            var header = TableHeader(serverName);
            var childPrefix = "[mcp_servers." + serverName + ".";
            var lines = SplitLines(original);
            var body = BuildBody(serverName, url, token);

            var start = IndexOfHeader(lines, header);
            var replaced = start >= 0;

            if (replaced)
            {
                var end = start + 1;

                while (end < lines.Count)
                {
                    var trimmed = lines[end].Trim();

                    if (trimmed.Length > 0 && trimmed[0] == '[' && !trimmed.StartsWith(childPrefix, StringComparison.Ordinal))
                    {
                        break;
                    }

                    ++end;
                }

                while (end - 1 > start && lines[end - 1].Trim().Length == 0)
                {
                    --end;
                }

                lines.RemoveRange(start, end - start);
                lines.InsertRange(start, body);
            }
            else
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.AddRange(body);
            }

            var content = string.Join(newLine, lines) + newLine;

            if (exists && string.Equals(content, original, StringComparison.Ordinal))
            {
                return new TomlWriteOutcome
                {
                    Created = false,
                    Replaced = replaced,
                    Changed = false
                };
            }

            AtomicFile.Write(path, content);

            return new TomlWriteOutcome
            {
                Created = !exists,
                Replaced = replaced,
                Changed = true
            };
        }

        public static bool TryReadUrl(string path, string serverName, out string url)
        {
            url = null;

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(serverName) || !File.Exists(path))
            {
                return false;
            }

            List<string> lines;

            try
            {
                lines = SplitLines(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            var start = IndexOfHeader(lines, TableHeader(serverName));

            if (start < 0)
            {
                return false;
            }

            for (var i = start + 1; i < lines.Count; ++i)
            {
                var trimmed = lines[i].Trim();

                if (trimmed.Length > 0 && trimmed[0] == '[')
                {
                    break;
                }

                var separator = trimmed.IndexOf('=');

                if (separator < 0 || !string.Equals(trimmed.Substring(0, separator).Trim(), "url", StringComparison.Ordinal))
                {
                    continue;
                }

                url = Unquote(trimmed.Substring(separator + 1).Trim());

                return url != null;
            }

            return false;
        }

        private static string TableHeader(string serverName)
        {
            return "[mcp_servers." + serverName + "]";
        }

        private static List<string> BuildBody(string serverName, string url, string token)
        {
            return new List<string>
            {
                TableHeader(serverName),
                "type = \"http\"",
                "url = \"" + Escape(url) + "\"",
                string.Empty,
                "[mcp_servers." + serverName + ".headers]",
                "Authorization = \"Bearer " + Escape(token) + "\""
            };
        }

        private static int IndexOfHeader(List<string> lines, string header)
        {
            for (var i = 0; i < lines.Count; ++i)
            {
                if (string.Equals(lines[i].Trim(), header, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();

            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }

            var parts = text.Split('\n');

            for (var i = 0; i < parts.Length; ++i)
            {
                var part = parts[i];

                if (part.Length > 0 && part[part.Length - 1] == '\r')
                {
                    part = part.Substring(0, part.Length - 1);
                }

                if (i == parts.Length - 1 && part.Length == 0)
                {
                    break;
                }

                lines.Add(part);
            }

            return lines;
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Unquote(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"')
            {
                return null;
            }

            var builder = new StringBuilder(value.Length - 2);
            var last = value.Length - 1;

            for (var i = 1; i < last; ++i)
            {
                if (value[i] == '\\' && i + 1 < last)
                {
                    ++i;
                }

                builder.Append(value[i]);
            }

            return builder.ToString();
        }
    }
}
