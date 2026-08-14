using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Protocol
{
    internal sealed class CachedOutput
    {
        public string Id { get; }

        public string Payload { get; }

        public DateTime CreatedUtc { get; }

        public CachedOutput(string id, string payload)
        {
            Id = id;
            Payload = payload;
            CreatedUtc = DateTime.UtcNow;
        }
    }

    internal sealed class OutputCache
    {
        //
        // A large structuredContent does not fail, it just eats the model's context window
        // and leaves nothing for the reasoning that was supposed to use it. Anything over
        // the character cap is parked here under a guid and the caller gets a preview plus
        // an outputId, then pulls the rest through get_output in slices it picks itself.
        //
        // The store is a plain FIFO of 32 entries. It is a scratch buffer for one session,
        // not a cache with a hit rate worth protecting, so the oldest entry leaves without
        // ceremony when the 33rd arrives.
        //
        public const int MaxCharacters = 50000;

        private const int MaxEntries = 32;

        private const int PreviewItems = 10;

        private const int PreviewCharacters = 1000;

        private const int PreviewDepth = 5;

        private readonly object sync = new object();

        private readonly Dictionary<string, CachedOutput> entries = new Dictionary<string, CachedOutput>(StringComparer.Ordinal);

        private readonly Queue<string> order = new Queue<string>();

        public bool TryGet(string id, out CachedOutput output)
        {
            lock (sync)
            {
                return entries.TryGetValue(id, out output);
            }
        }

        public JObject Compact(JObject structured, out JObject meta)
        {
            meta = null;

            if (structured == null)
            {
                return null;
            }

            var serialized = structured.ToString(Formatting.None);
            if (serialized.Length <= MaxCharacters)
            {
                return structured;
            }

            var id = Guid.NewGuid().ToString("N");
            Store(new CachedOutput(id, serialized));

            meta = new JObject
            {
                ["net.reclass/truncated"] = new JObject
                {
                    ["outputId"] = id,
                    ["totalCharacters"] = serialized.Length,
                    ["limit"] = MaxCharacters,
                    ["retrieveWith"] = "get_output"
                }
            };

            return (JObject)Preview(structured, 0);
        }

        private void Store(CachedOutput output)
        {
            lock (sync)
            {
                entries[output.Id] = output;
                order.Enqueue(output.Id);

                while (order.Count > MaxEntries)
                {
                    var evicted = order.Dequeue();
                    entries.Remove(evicted);
                }
            }
        }

        private static JToken Preview(JToken token, int depth)
        {
            if (depth >= PreviewDepth)
            {
                return JValue.CreateNull();
            }

            switch (token.Type)
            {
                case JTokenType.Object:
                {
                    var source = (JObject)token;
                    var result = new JObject();

                    foreach (var property in source.Properties())
                    {
                        result[property.Name] = Preview(property.Value, depth + 1);
                    }

                    return result;
                }

                case JTokenType.Array:
                {
                    //
                    // No sentinel element is appended to mark the cut. Tool output schemas
                    // pin item shapes with additionalProperties: false, so an extra
                    // "truncated" object would fail the client's structured-output check
                    // and cost the whole result. The cut is reported once, in _meta, where
                    // nothing validates it away.
                    //
                    var source = (JArray)token;
                    var result = new JArray();
                    var take = Math.Min(PreviewItems, source.Count);

                    for (var i = 0; i < take; ++i)
                    {
                        result.Add(Preview(source[i], depth + 1));
                    }

                    return result;
                }

                case JTokenType.String:
                {
                    var value = (string)token;
                    if (value != null && value.Length > PreviewCharacters)
                    {
                        return value.Substring(0, PreviewCharacters);
                    }

                    return token.DeepClone();
                }

                default:
                    return token.DeepClone();
            }
        }
    }
}
