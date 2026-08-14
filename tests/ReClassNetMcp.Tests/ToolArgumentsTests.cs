using Newtonsoft.Json.Linq;
using ReClassNetMcp.Tools;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class ToolArgumentsTests
    {
        private static ToolArguments Parse(string json)
        {
            return new ToolArguments(JObject.Parse(json));
        }

        [Theory]
        [InlineData("0x1f4", 0x1f4)]
        [InlineData("1f4", 0x1f4)]
        [InlineData("0X1F4", 0x1f4)]
        [InlineData("  0x10  ", 0x10)]
        [InlineData("0", 0)]
        public void AddressAcceptsHexWithAndWithoutPrefix(string value, long expected)
        {
            var arguments = new ToolArguments(new JObject { ["address"] = value });

            Assert.Equal(expected, arguments.Address("address").ToInt64());
        }

        [Theory]
        [InlineData("zz")]
        [InlineData("")]
        [InlineData("0x")]
        [InlineData("0x1f4g")]
        [InlineData("00000000000000001")]
        public void AddressRejectsNonHexadecimalText(string value)
        {
            var arguments = new ToolArguments(new JObject { ["address"] = value });

            Assert.Throws<InvalidArgumentsException>(() => arguments.Address("address"));
        }

        [Fact]
        public void CountRejectsANegativeValue()
        {
            var arguments = new ToolArguments(new JObject { ["limit"] = -1 });

            var failure = Assert.Throws<InvalidArgumentsException>(() => arguments.Count("limit", 100, 1000));

            Assert.Contains("limit", failure.Message);
        }

        [Fact]
        public void CountRejectsAValueOverTheMaximum()
        {
            var arguments = new ToolArguments(new JObject { ["limit"] = 1001 });

            var failure = Assert.Throws<InvalidArgumentsException>(() => arguments.Count("limit", 100, 1000));

            Assert.Contains("1000", failure.Message);
        }

        [Fact]
        public void CountAcceptsTheBoundsAndFallsBackWhenAbsent()
        {
            Assert.Equal(1000, new ToolArguments(new JObject { ["limit"] = 1000 }).Count("limit", 100, 1000));
            Assert.Equal(0, new ToolArguments(new JObject { ["limit"] = 0 }).Count("limit", 100, 1000));
            Assert.Equal(100, new ToolArguments(new JObject()).Count("limit", 100, 1000));
        }

        [Fact]
        public void ObjectsAcceptsASingleObject()
        {
            var items = Parse("{\"nodes\":{\"handle\":\"a\"}}").Objects("nodes");

            Assert.Single(items);
            Assert.Equal("a", (string)items[0]["handle"]);
        }

        [Fact]
        public void ObjectsAcceptsAnArrayOfObjects()
        {
            var items = Parse("{\"nodes\":[{\"handle\":\"a\"},{\"handle\":\"b\"}]}").Objects("nodes");

            Assert.Equal(2, items.Count);
            Assert.Equal("a", (string)items[0]["handle"]);
            Assert.Equal("b", (string)items[1]["handle"]);
        }

        [Fact]
        public void ObjectsRejectsScalarEntries()
        {
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"nodes\":[1,2]}").Objects("nodes"));
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"nodes\":\"a\"}").Objects("nodes"));
        }

        [Fact]
        public void ObjectsRequiresThePropertyToBePresent()
        {
            Assert.Throws<InvalidArgumentsException>(() => Parse("{}").Objects("nodes"));
        }

        [Fact]
        public void DataReadsHexPayloads()
        {
            Assert.Equal(new byte[] { 0x0a, 0x0b }, Parse("{\"hex\":\"0a0B\"}").Data());
            Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, Parse("{\"hex\":\"DE AD-BE EF\"}").Data());
            Assert.Equal(new byte[] { 0x01, 0x02 }, Parse("{\"hex\":\"0x0102\"}").Data());
        }

        [Fact]
        public void DataReadsBase64Payloads()
        {
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, Parse("{\"base64\":\"AQID\"}").Data());
        }

        [Fact]
        public void DataRejectsAnOddLengthHexString()
        {
            var failure = Assert.Throws<InvalidArgumentsException>(() => Parse("{\"hex\":\"abc\"}").Data());

            Assert.Contains("even length", failure.Message);
        }

        [Fact]
        public void DataRejectsInvalidBase64()
        {
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"base64\":\"not valid base64!\"}").Data());
        }

        [Fact]
        public void DataRequiresAPayloadArgument()
        {
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"size\":4}").Data());
        }

        [Fact]
        public void MissingRequiredArgumentsAreNamedInTheFailure()
        {
            var arguments = new ToolArguments(new JObject());

            Assert.Contains("handle", Assert.Throws<InvalidArgumentsException>(() => arguments.String("handle")).Message);
            Assert.Contains("size", Assert.Throws<InvalidArgumentsException>(() => arguments.Integer("size")).Message);
            Assert.Contains("uuid", Assert.Throws<InvalidArgumentsException>(() => arguments.Uuid("uuid")).Message);
            Assert.Contains("address", Assert.Throws<InvalidArgumentsException>(() => arguments.Address("address")).Message);
        }

        [Fact]
        public void OptionalAccessorsUseTheFallbackAndHasReportsPresence()
        {
            var arguments = Parse("{\"name\":\"value\",\"empty\":null}");

            Assert.True(arguments.Has("name"));
            Assert.False(arguments.Has("empty"));
            Assert.False(arguments.Has("absent"));
            Assert.Equal("value", arguments.OptionalString("name", "fallback"));
            Assert.Equal("fallback", arguments.OptionalString("empty", "fallback"));
            Assert.Equal(7L, arguments.OptionalInteger("absent", 7));
            Assert.True(arguments.Bool("absent", true));
        }

        [Fact]
        public void WrongTypesAreRejectedRatherThanCoerced()
        {
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"name\":5}").String("name"));
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"size\":\"abc\"}").Integer("size"));
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"flag\":\"true\"}").Bool("flag", false));
            Assert.Throws<InvalidArgumentsException>(() => Parse("{\"uuid\":\"nope\"}").Uuid("uuid"));
        }
    }
}
