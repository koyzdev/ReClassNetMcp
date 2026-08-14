using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Protocol
{
    internal sealed class ResourceDefinition
    {
        public string Uri { get; }

        public string Name { get; }

        public string Title { get; }

        public string Description { get; }

        public Func<string, CancellationToken, JObject> Reader { get; }

        public ResourceDefinition(string uri, string name, string title, string description, Func<string, CancellationToken, JObject> reader)
        {
            Uri = uri;
            Name = name;
            Title = title;
            Description = description;
            Reader = reader;
        }

        public bool IsTemplate => Uri.IndexOf('{') >= 0;
    }

    internal sealed class ResourceRegistry
    {
        private readonly List<ResourceDefinition> definitions = new List<ResourceDefinition>();

        public void Add(ResourceDefinition definition)
        {
            definitions.Add(definition);
        }

        public JArray DescribeResources()
        {
            var array = new JArray();

            foreach (var definition in definitions)
            {
                if (definition.IsTemplate)
                {
                    continue;
                }

                array.Add(new JObject
                {
                    ["uri"] = definition.Uri,
                    ["name"] = definition.Name,
                    ["title"] = definition.Title,
                    ["description"] = definition.Description,
                    ["mimeType"] = "application/json"
                });
            }

            return array;
        }

        public JArray DescribeTemplates()
        {
            var array = new JArray();

            foreach (var definition in definitions)
            {
                if (!definition.IsTemplate)
                {
                    continue;
                }

                array.Add(new JObject
                {
                    ["uriTemplate"] = definition.Uri,
                    ["name"] = definition.Name,
                    ["title"] = definition.Title,
                    ["description"] = definition.Description,
                    ["mimeType"] = "application/json"
                });
            }

            return array;
        }

        public JObject Read(JObject parameters, CancellationToken token)
        {
            var uri = parameters["uri"];
            if (uri == null || uri.Type != JTokenType.String)
            {
                throw new InvalidArgumentsException("'uri' must be a string");
            }

            var requested = (string)uri;

            foreach (var definition in definitions)
            {
                if (!TryMatch(definition.Uri, requested, out var argument))
                {
                    continue;
                }

                var payload = definition.Reader(argument, token);

                return new JObject
                {
                    ["contents"] = new JArray
                    {
                        new JObject
                        {
                            ["uri"] = requested,
                            ["name"] = definition.Name,
                            ["mimeType"] = "application/json",
                            ["text"] = payload.ToString(Formatting.None)
                        }
                    }
                };
            }

            throw new ToolException($"Unknown resource uri '{requested}'");
        }

        private static bool TryMatch(string pattern, string requested, out string argument)
        {
            //
            // Deliberately not RFC 6570. Exactly one {name} capture per template, no
            // operators, no exploded values, no query expansion. The only template that
            // exists is reclass://class/{uuid}, and a general expander would be more code
            // than the entire resource surface it would serve.
            //

            argument = null;

            var start = pattern.IndexOf('{');
            if (start < 0)
            {
                return string.Equals(pattern, requested, StringComparison.Ordinal);
            }

            var end = pattern.IndexOf('}', start);
            if (end < 0)
            {
                return false;
            }

            var prefix = pattern.Substring(0, start);
            var suffix = pattern.Substring(end + 1);

            if (!requested.StartsWith(prefix, StringComparison.Ordinal) || !requested.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var length = requested.Length - prefix.Length - suffix.Length;
            if (length <= 0)
            {
                return false;
            }

            argument = requested.Substring(prefix.Length, length);
            return true;
        }
    }
}
