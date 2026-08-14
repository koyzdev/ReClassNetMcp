using ReClassNetMcp.Server;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class TokenComparerTests
    {
        private const string Token = "b7d2c1a0f39e4d5b8c6a1e2f3049abcd";

        [Fact]
        public void ExactMatchSucceedsForDistinctStringInstances()
        {
            Assert.True(TokenComparer.Matches(Token, Token));
            Assert.True(TokenComparer.Matches(Token, new string(Token.ToCharArray())));
        }

        [Theory]
        [InlineData("wrong")]
        [InlineData("")]
        [InlineData("b7d2c1a0f39e4d5b")]
        [InlineData("b7d2c1a0f39e4d5b8c6a1e2f3049abc")]
        [InlineData("b7d2c1a0f39e4d5b8c6a1e2f3049abcd ")]
        [InlineData(" b7d2c1a0f39e4d5b8c6a1e2f3049abcd")]
        [InlineData("b7d2c1a0f39e4d5b8c6a1e2f3049abcde")]
        [InlineData("B7D2C1A0F39E4D5B8C6A1E2F3049ABCD")]
        public void WrongTokenFails(string provided)
        {
            Assert.False(TokenComparer.Matches(Token, provided));
        }

        [Fact]
        public void NullNeverMatches()
        {
            Assert.False(TokenComparer.Matches(null, Token));
            Assert.False(TokenComparer.Matches(Token, null));
            Assert.False(TokenComparer.Matches(null, null));
        }
    }
}
