using System;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Protocol
{
    internal sealed class McpDispatcher
    {
        private const string ServerName = "reclass.net";

        private readonly ToolRegistry tools;

        private readonly ResourceRegistry resources;

        private readonly OutputCache outputCache;

        private readonly IReClassHost host;

        private readonly Func<bool> allowMutations;

        private readonly ConcurrentDictionary<string, CancellationTokenSource> inflight =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        private string negotiatedVersion = ProtocolVersions.Advertised;

        public McpDispatcher(IReClassHost host, ToolRegistry tools, ResourceRegistry resources, OutputCache outputCache, Func<bool> allowMutations)
        {
            this.host = host;
            this.tools = tools;
            this.resources = resources;
            this.outputCache = outputCache;
            this.allowMutations = allowMutations;
        }

        public string NegotiatedVersion => negotiatedVersion;

        public OutputCache OutputCache => outputCache;

        public JObject Handle(JsonRpcRequest request)
        {
            //
            // A notification carries no id and must never be answered, not even with an
            // error. Returning null is the signal to the transport, which then closes the
            // exchange with 202 and an empty body.
            //
            // A real request registers its cancellation source under the id key for as
            // long as it runs, because that key is the only handle notifications/cancelled
            // has to reach it.
            //
            if (request.IsNotification)
            {
                HandleNotification(request);
                return null;
            }

            var key = JsonRpc.IdKey(request.Id);

            using (var cancellation = new CancellationTokenSource())
            {
                if (key != null)
                {
                    inflight[key] = cancellation;
                }

                try
                {
                    return HandleRequest(request, cancellation.Token);
                }
                catch (InvalidArgumentsException ex)
                {
                    return JsonRpc.Error(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    return JsonRpc.Error(request.Id, JsonRpcErrorCode.InternalError, "The request was cancelled");
                }
                catch (Exception ex)
                {
                    host.Log(HostLogLevel.Error, $"mcp: {request.Method} failed: {ex}");
                    return JsonRpc.Error(request.Id, JsonRpcErrorCode.InternalError, ex.Message);
                }
                finally
                {
                    if (key != null)
                    {
                        inflight.TryRemove(key, out _);
                    }
                }
            }
        }

        private void HandleNotification(JsonRpcRequest request)
        {
            if (request.Method == "notifications/cancelled")
            {
                var id = request.Params["requestId"];
                var key = JsonRpc.IdKey(id);

                if (key != null && inflight.TryGetValue(key, out var cancellation))
                {
                    cancellation.Cancel();
                }
            }
        }

        private JObject HandleRequest(JsonRpcRequest request, CancellationToken token)
        {
            switch (request.Method)
            {
                case "initialize":
                    return JsonRpc.Result(request.Id, Initialize(request.Params));

                case "ping":
                    return JsonRpc.Result(request.Id, new JObject());

                case "tools/list":
                    return JsonRpc.Result(request.Id, new JObject { ["tools"] = tools.Describe(allowMutations()) });

                case "tools/call":
                    return JsonRpc.Result(request.Id, CallTool(request.Params, token));

                case "resources/list":
                    return JsonRpc.Result(request.Id, new JObject { ["resources"] = resources.DescribeResources() });

                case "resources/templates/list":
                    return JsonRpc.Result(request.Id, new JObject { ["resourceTemplates"] = resources.DescribeTemplates() });

                case "resources/read":
                    return JsonRpc.Result(request.Id, resources.Read(request.Params, token));

                case "prompts/list":
                    return JsonRpc.Result(request.Id, new JObject { ["prompts"] = new JArray() });

                case "logging/setLevel":
                    return JsonRpc.Result(request.Id, new JObject());

                default:
                    return JsonRpc.Error(request.Id, JsonRpcErrorCode.MethodNotFound, $"Unknown method '{request.Method}'");
            }
        }

        private JObject Initialize(JObject parameters)
        {
            //
            // An unknown revision is a successful result, not an error: answer with the
            // advertised one and let the client decide whether to keep going. That is the
            // negotiation the pre-stateless revisions specify.
            //
            // The two empty capability objects further down are deliberate. Declaring
            // tools and resources with no listChanged, no subscribe and no logging leaves
            // the server nothing it could ever push, which is what keeps SSE and every
            // server-initiated message out of the transport.
            //
            var requested = parameters["protocolVersion"];
            negotiatedVersion = ProtocolVersions.Negotiate(requested?.Type == JTokenType.String ? (string)requested : null);

            var clientName = parameters["clientInfo"]?["name"];
            host.Log(HostLogLevel.Information, $"mcp: client '{clientName ?? "unknown"}' connected using protocol {negotiatedVersion}");

            return new JObject
            {
                ["protocolVersion"] = negotiatedVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject(),
                    ["resources"] = new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["title"] = "ReClass.NET",
                    ["version"] = PluginVersion.Value
                },
                ["instructions"] = Instructions.Text
            };
        }

        private JObject CallTool(JObject parameters, CancellationToken token)
        {
            var name = parameters["name"];
            if (name == null || name.Type != JTokenType.String)
            {
                throw new InvalidArgumentsException("'name' must be a string");
            }

            if (!tools.TryGet((string)name, out var tool))
            {
                throw new InvalidArgumentsException($"Unknown tool '{(string)name}'");
            }

            if (tool.RequiresMutations && !allowMutations())
            {
                return BuildResult(ToolResult.Failure(
                    $"The tool '{tool.Name}' is disabled because mutations are turned off",
                    "Enable 'MCP Server -> Allow mutations' in ReClass.NET."));
            }

            var arguments = new ToolArguments(parameters["arguments"] as JObject);

            //
            // Both exception classes collapse into an isError result here instead of a
            // JSON-RPC error, because the model can read a result and retry with better
            // arguments. The two throws above are the other case: a missing 'name' or an
            // unknown tool escapes to Handle and becomes -32602, since retrying the same
            // shape cannot fix either one.
            //
            ToolResult result;
            try
            {
                result = tool.Handler(arguments, token);
            }
            catch (InvalidArgumentsException ex)
            {
                result = ToolResult.Failure(ex.Message, null);
            }
            catch (ToolException ex)
            {
                result = ToolResult.Failure(ex.Message, ex.Hint);
            }

            if (!tool.Annotations.ReadOnly && !result.IsError)
            {
                host.Log(HostLogLevel.Information, $"mcp: {tool.Name} {arguments}");
            }

            return BuildResult(result);
        }

        private JObject BuildResult(ToolResult result)
        {
            //
            // The text block is not decoration. A client on a revision below 2025-06-18
            // never receives structuredContent, so a compact serialization of the same
            // object is the only channel it has, and any tool that supplied no text of
            // its own gets one built here.
            //
            var structured = outputCache.Compact(result.Structured, out var meta);

            var text = result.Text;
            if (text == null && structured != null)
            {
                text = structured.ToString(Newtonsoft.Json.Formatting.None);
            }

            var content = new JArray();
            if (!string.IsNullOrEmpty(text))
            {
                content.Add(new JObject { ["type"] = "text", ["text"] = text });
            }

            var payload = new JObject
            {
                ["content"] = content,
                ["isError"] = result.IsError
            };

            if (structured != null && ProtocolVersions.SupportsStructuredContent(negotiatedVersion))
            {
                payload["structuredContent"] = structured;
            }

            if (meta != null)
            {
                payload["_meta"] = meta;
            }

            return payload;
        }
    }
}
