using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Configuration;

namespace ReClassNetMcp.Install
{
    internal sealed class JsonWriteOutcome
    {
        public bool Created { get; set; }

        public bool Replaced { get; set; }

        public bool Changed { get; set; }
    }

    internal static class JsonConfigWriter
    {
        public static JsonWriteOutcome Write(string path, string containerKey, string serverName, JObject entry, string schemaUrl)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("The configuration path is required.", nameof(path));
            }

            if (string.IsNullOrEmpty(containerKey))
            {
                throw new ArgumentException("The container key is required.", nameof(containerKey));
            }

            if (string.IsNullOrEmpty(serverName))
            {
                throw new ArgumentException("The server name is required.", nameof(serverName));
            }

            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var exists = File.Exists(path);
            var document = exists ? ReadDocument(path) : new JObject();

            var containerToken = document[containerKey];
            var container = containerToken as JObject;
            var containerAdded = false;

            if (container == null)
            {
                if (containerToken != null && containerToken.Type != JTokenType.Null)
                {
                    throw new InvalidOperationException($"'{path}' holds a '{containerKey}' value that is not an object. The file was left untouched; fix it, then install again.");
                }

                container = new JObject();
                document[containerKey] = container;
                containerAdded = true;
            }

            var existingEntry = container[serverName];
            var replaced = existingEntry != null && existingEntry.Type != JTokenType.Null;
            var entryChanged = !replaced || !JToken.DeepEquals(existingEntry, entry);

            container[serverName] = entry;

            var schemaAdded = false;

            if (!string.IsNullOrEmpty(schemaUrl) && document["$schema"] == null)
            {
                document.AddFirst(new JProperty("$schema", schemaUrl));
                schemaAdded = true;
            }

            if (exists && !entryChanged && !containerAdded && !schemaAdded)
            {
                return new JsonWriteOutcome
                {
                    Created = false,
                    Replaced = replaced,
                    Changed = false
                };
            }

            AtomicFile.Write(path, document.ToString(Formatting.Indented) + Environment.NewLine);

            return new JsonWriteOutcome
            {
                Created = !exists,
                Replaced = replaced,
                Changed = true
            };
        }

        public static bool TryReadExistingUrl(string path, string containerKey, string serverName, out string url)
        {
            url = null;

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(containerKey) || string.IsNullOrEmpty(serverName) || !File.Exists(path))
            {
                return false;
            }

            JObject document;

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                document = JObject.Parse(text);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }

            var container = document[containerKey] as JObject;

            if (container == null)
            {
                return false;
            }

            var entry = container[serverName] as JObject;

            if (entry == null)
            {
                return false;
            }

            url = entry.Value<string>("url");

            return !string.IsNullOrEmpty(url);
        }

        private static JObject ReadDocument(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);

            if (string.IsNullOrWhiteSpace(text))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(text);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"'{path}' is not valid JSON and was left untouched: {exception.Message}. Fix the file, then install again.", exception);
            }
        }
    }
}
