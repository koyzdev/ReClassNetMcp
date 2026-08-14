using System;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Protocol
{
    internal static class JsonRpcErrorCode
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }

    internal sealed class JsonRpcRequest
    {
        public JToken Id { get; }

        public string Method { get; }

        public JObject Params { get; }

        public bool IsNotification => Id == null;

        private JsonRpcRequest(JToken id, string method, JObject parameters)
        {
            Id = id;
            Method = method;
            Params = parameters;
        }

        public static bool TryParse(JToken payload, out JsonRpcRequest request, out JObject error)
        {
            request = null;
            error = null;

            //
            // Batching left the protocol in the 2025-06-18 revision, so a JSON array is
            // refused outright instead of being iterated. The two older revisions still in
            // the supported set allowed it, but nothing that speaks them is missing the
            // single-object path, so a second framing path would only be dead weight.
            //
            if (!(payload is JObject obj))
            {
                error = JsonRpc.Error(null, JsonRpcErrorCode.InvalidRequest, "Expected a single JSON-RPC object, arrays and batches are not supported");
                return false;
            }

            var id = obj["id"];
            if (id != null && id.Type == JTokenType.Null)
            {
                id = null;
            }

            var version = obj["jsonrpc"];
            if (version == null || version.Type != JTokenType.String || (string)version != "2.0")
            {
                error = JsonRpc.Error(id, JsonRpcErrorCode.InvalidRequest, "Missing or invalid 'jsonrpc' member, expected \"2.0\"");
                return false;
            }

            //
            // No 'method' but a 'result' or an 'error' means the client is answering us,
            // not calling us. There is nothing to reply to and no id of ours involved, so
            // this returns false with no error object and the transport turns that into
            // 202 with an empty body.
            //
            if (obj["method"] == null)
            {
                if (obj["result"] != null || obj["error"] != null)
                {
                    return false;
                }

                error = JsonRpc.Error(id, JsonRpcErrorCode.InvalidRequest, "Missing 'method' member");
                return false;
            }

            var method = obj["method"];
            if (method.Type != JTokenType.String)
            {
                error = JsonRpc.Error(id, JsonRpcErrorCode.InvalidRequest, "'method' must be a string");
                return false;
            }

            if (id != null && id.Type != JTokenType.String && id.Type != JTokenType.Integer && id.Type != JTokenType.Float)
            {
                error = JsonRpc.Error(null, JsonRpcErrorCode.InvalidRequest, "'id' must be a string or a number");
                return false;
            }

            var rawParams = obj["params"];
            JObject parameters;
            if (rawParams == null || rawParams.Type == JTokenType.Null)
            {
                parameters = new JObject();
            }
            else if (rawParams is JObject paramsObject)
            {
                parameters = paramsObject;
            }
            else
            {
                error = JsonRpc.Error(id, JsonRpcErrorCode.InvalidParams, "'params' must be an object");
                return false;
            }

            request = new JsonRpcRequest(id, (string)method, parameters);
            return true;
        }
    }

    internal static class JsonRpc
    {
        public static JObject Result(JToken id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result ?? new JObject()
            };
        }

        public static JObject Error(JToken id, int code, string message, JToken data = null)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };

            if (data != null)
            {
                error["data"] = data;
            }

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = error
            };
        }

        public static string IdKey(JToken id)
        {
            if (id == null)
            {
                return null;
            }

            //
            // The type is folded into the key because JSON-RPC treats the string "7" and
            // the number 7 as two different ids. Without it a cancellation aimed at one
            // would land on the other.
            //
            return id.Type == JTokenType.String ? "s:" + (string)id : "n:" + id.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
