using ReClassNetMcp.Protocol;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class ProtocolVersionTests
    {
        [Theory]
        [InlineData("2025-11-25")]
        [InlineData("2025-06-18")]
        [InlineData("2025-03-26")]
        [InlineData("2024-11-05")]
        public void NegotiateEchoesSupportedVersion(string version)
        {
            Assert.True(ProtocolVersions.IsSupported(version));
            Assert.Equal(version, ProtocolVersions.Negotiate(version));
        }

        [Theory]
        [InlineData("2026-01-01")]
        [InlineData("2023-01-01")]
        [InlineData("garbage")]
        [InlineData("")]
        [InlineData((string)null)]
        public void NegotiateFallsBackToAdvertisedVersion(string version)
        {
            Assert.False(ProtocolVersions.IsSupported(version));
            Assert.Equal("2025-11-25", ProtocolVersions.Negotiate(version));
        }

        [Fact]
        public void AdvertisedVersionIsTheNewestSupportedOne()
        {
            Assert.Equal("2025-11-25", ProtocolVersions.Advertised);
            Assert.Contains(ProtocolVersions.Advertised, ProtocolVersions.Supported);
            Assert.Equal(ProtocolVersions.Advertised, ProtocolVersions.Supported[0]);
        }

        [Theory]
        [InlineData("2025-11-25", true)]
        [InlineData("2025-06-18", true)]
        [InlineData("2025-03-26", false)]
        [InlineData("2024-11-05", false)]
        public void StructuredContentIsGatedOnTheNegotiatedVersion(string version, bool expected)
        {
            Assert.Equal(expected, ProtocolVersions.SupportsStructuredContent(version));
        }

        [Fact]
        public void MissingHeaderAssumesAVersionWithoutStructuredContent()
        {
            Assert.Equal("2025-03-26", ProtocolVersions.AssumedWhenHeaderMissing);
            Assert.False(ProtocolVersions.SupportsStructuredContent(ProtocolVersions.AssumedWhenHeaderMissing));
        }
    }
}
