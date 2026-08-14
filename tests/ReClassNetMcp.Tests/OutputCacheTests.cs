using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Protocol;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class OutputCacheTests
    {
        [Fact]
        public void SmallPayloadPassesThroughUntouched()
        {
            var cache = new OutputCache();
            var structured = new JObject
            {
                ["items"] = new JArray { "a", "b", "c" },
                ["total"] = 3
            };

            var result = cache.Compact(structured, out var meta);

            Assert.Null(meta);
            Assert.Same(structured, result);
        }

        [Fact]
        public void OversizedPayloadIsCachedAndDescribedInMeta()
        {
            var cache = new OutputCache();
            var structured = BuildOversizedPayload();
            var serialized = structured.ToString(Formatting.None);

            var preview = cache.Compact(structured, out var meta);

            Assert.NotNull(meta);
            Assert.True(serialized.Length > OutputCache.MaxCharacters);

            var truncated = (JObject)meta["net.reclass/truncated"];
            var id = (string)truncated["outputId"];

            Assert.False(string.IsNullOrEmpty(id));
            Assert.Equal(serialized.Length, (int)truncated["totalCharacters"]);
            Assert.Equal(OutputCache.MaxCharacters, (int)truncated["limit"]);
            Assert.Equal("get_output", (string)truncated["retrieveWith"]);

            Assert.True(cache.TryGet(id, out var cached));
            Assert.Equal(id, cached.Id);
            Assert.Equal(serialized, cached.Payload);

            Assert.NotSame(structured, preview);
            Assert.True(preview.ToString(Formatting.None).Length < serialized.Length);
        }

        [Fact]
        public void PreviewTruncatesArraysToTenItemsWithoutASentinel()
        {
            var cache = new OutputCache();

            var preview = cache.Compact(BuildOversizedPayload(), out var meta);

            Assert.NotNull(meta);

            var items = (JArray)preview["items"];

            Assert.Equal(10, items.Count);

            foreach (var item in items)
            {
                Assert.Equal(JTokenType.String, item.Type);
                Assert.Equal(1000, ((string)item).Length);
            }
        }

        [Fact]
        public void PreviewTruncatesLongStringsAndKeepsShortOnes()
        {
            var cache = new OutputCache();
            var structured = new JObject
            {
                ["long"] = new string('a', OutputCache.MaxCharacters + 100),
                ["short"] = "kept"
            };

            var preview = cache.Compact(structured, out var meta);

            Assert.NotNull(meta);
            Assert.Equal(1000, ((string)preview["long"]).Length);
            Assert.Equal("kept", (string)preview["short"]);
        }

        [Fact]
        public void UnknownOutputIdIsNotFound()
        {
            var cache = new OutputCache();

            Assert.False(cache.TryGet("00000000000000000000000000000000", out var cached));
            Assert.Null(cached);
        }

        [Fact]
        public void EvictionDropsTheOldestEntryAfterThirtyTwoStores()
        {
            var cache = new OutputCache();
            var ids = new List<string>();

            for (var i = 0; i < 33; ++i)
            {
                var structured = new JObject { ["value"] = new string('x', OutputCache.MaxCharacters + 16) };

                cache.Compact(structured, out var meta);

                ids.Add((string)meta["net.reclass/truncated"]["outputId"]);
            }

            Assert.Equal(33, ids.Count);
            Assert.False(cache.TryGet(ids[0], out _));

            for (var i = 1; i < ids.Count; ++i)
            {
                Assert.True(cache.TryGet(ids[i], out _));
            }
        }

        private static JObject BuildOversizedPayload()
        {
            var items = new JArray();

            for (var i = 0; i < 60; ++i)
            {
                items.Add(new string((char)('a' + i % 26), 1200));
            }

            return new JObject
            {
                ["items"] = items,
                ["total"] = 60
            };
        }
    }
}
