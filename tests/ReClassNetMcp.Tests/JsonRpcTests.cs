using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Protocol;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class JsonRpcTests
    {
        [Fact]
        public void ValidRequestExposesIdMethodAndParams()
        {
            var payload = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"status\"}}");

            Assert.True(JsonRpcRequest.TryParse(payload, out var request, out var error));
            Assert.Null(error);
            Assert.Equal(7, (int)request.Id);
            Assert.Equal("tools/call", request.Method);
            Assert.Equal("status", (string)request.Params["name"]);
            Assert.False(request.IsNotification);
        }

        [Fact]
        public void MissingParamsBecomesEmptyObject()
        {
            var payload = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

            Assert.True(JsonRpcRequest.TryParse(payload, out var request, out _));
            Assert.NotNull(request.Params);
            Assert.Empty(request.Params);
        }

        [Fact]
        public void BatchArrayIsRejectedAsInvalidRequest()
        {
            var payload = JArray.Parse("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}]");

            Assert.False(JsonRpcRequest.TryParse(payload, out var request, out var error));
            Assert.Null(request);
            Assert.NotNull(error);
            Assert.Equal(JsonRpcErrorCode.InvalidRequest, (int)error["error"]["code"]);
            Assert.Equal(JTokenType.Null, error["id"].Type);
        }

        [Fact]
        public void MissingJsonRpcMemberIsRejected()
        {
            var payload = JObject.Parse("{\"id\":1,\"method\":\"initialize\"}");

            Assert.False(JsonRpcRequest.TryParse(payload, out var request, out var error));
            Assert.Null(request);
            Assert.Equal(JsonRpcErrorCode.InvalidRequest, (int)error["error"]["code"]);
            Assert.Equal(1, (int)error["id"]);
        }

        [Fact]
        public void WrongJsonRpcVersionIsRejected()
        {
            var payload = JObject.Parse("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"initialize\"}");

            Assert.False(JsonRpcRequest.TryParse(payload, out _, out var error));
            Assert.Equal(JsonRpcErrorCode.InvalidRequest, (int)error["error"]["code"]);
        }

        [Fact]
        public void MissingMethodIsRejected()
        {
            var payload = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1}");

            Assert.False(JsonRpcRequest.TryParse(payload, out _, out var error));
            Assert.Equal(JsonRpcErrorCode.InvalidRequest, (int)error["error"]["code"]);
        }

        [Theory]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"notifications/initialized\"}")]
        public void RequestWithoutIdIsANotification(string json)
        {
            Assert.True(JsonRpcRequest.TryParse(JObject.Parse(json), out var request, out var error));
            Assert.Null(error);
            Assert.Null(request.Id);
            Assert.True(request.IsNotification);
        }

        [Theory]
        [InlineData("[1,2]")]
        [InlineData("\"text\"")]
        [InlineData("5")]
        [InlineData("true")]
        public void NonObjectParamsIsInvalidParams(string parameters)
        {
            var payload = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":" + parameters + "}");

            Assert.False(JsonRpcRequest.TryParse(payload, out var request, out var error));
            Assert.Null(request);
            Assert.Equal(JsonRpcErrorCode.InvalidParams, (int)error["error"]["code"]);
            Assert.Equal(1, (int)error["id"]);
        }

        [Fact]
        public void ErrorEmitsNullIdAsJsonNull()
        {
            var error = JsonRpc.Error(null, JsonRpcErrorCode.ParseError, "broken");

            Assert.True(error.ContainsKey("id"));
            Assert.Equal(JTokenType.Null, error["id"].Type);
            Assert.Contains("\"id\":null", error.ToString(Formatting.None));
            Assert.Equal("2.0", (string)error["jsonrpc"]);
            Assert.Equal("broken", (string)error["error"]["message"]);
            Assert.Null(error["error"]["data"]);
        }

        [Fact]
        public void ResultEmitsNullIdAsJsonNull()
        {
            var result = JsonRpc.Result(null, new JObject());

            Assert.True(result.ContainsKey("id"));
            Assert.Equal(JTokenType.Null, result["id"].Type);
            Assert.Contains("\"id\":null", result.ToString(Formatting.None));
        }

        [Fact]
        public void ErrorCarriesDataWhenSupplied()
        {
            var error = JsonRpc.Error(new JValue(3), JsonRpcErrorCode.InternalError, "boom", new JObject { ["hint"] = "retry" });

            Assert.Equal(3, (int)error["id"]);
            Assert.Equal("retry", (string)error["error"]["data"]["hint"]);
        }

        [Fact]
        public void IdKeyDistinguishesStringFromNumber()
        {
            var stringKey = JsonRpc.IdKey(new JValue("1"));
            var numberKey = JsonRpc.IdKey(new JValue(1));

            Assert.NotEqual(stringKey, numberKey);
            Assert.Equal("s:1", stringKey);
            Assert.Equal("n:1", numberKey);
        }

        [Fact]
        public void IdKeyOfNullIsNull()
        {
            Assert.Null(JsonRpc.IdKey(null));
        }
    }
}
