using System;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Protocol;

namespace ReClassNetMcp.Tools
{
    internal sealed class ServerTools
    {
        private readonly ToolContext context;

        public ServerTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "status",
                "Server status",
                "Report the ReClass.NET host version, platform, pointer size, MCP endpoint, whether mutations are allowed, the attached process and a summary of the open project. Call this first to learn the state of the session.",
                Schema.Object(),
                Schema.Object(
                    Schema.Required("server", Schema.AnyObject(), "Endpoint and plugin information"),
                    Schema.Required("host", Schema.AnyObject(), "ReClass.NET version, platform and pointer size"),
                    Schema.Required("process", Schema.AnyObject(), "Attached process, or attached=false"),
                    Schema.Required("project", Schema.AnyObject(), "Open project summary")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => Status()));

            registry.Add(new ToolDefinition(
                "get_output",
                "Get truncated output",
                "Retrieve a slice of a tool result that was too large to inline. Pass the outputId reported in the _meta of the truncated result.",
                Schema.Object(
                    Schema.Required("outputId", Schema.Text(), "The id from _meta['net.reclass/truncated'].outputId"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Character offset to start from, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, OutputCache.MaxCharacters), "Characters to return, default 20000")),
                Schema.Object(
                    Schema.Required("outputId", Schema.Text(), "Echo of the requested id"),
                    Schema.Required("offset", Schema.Integer(), "Offset of the returned slice"),
                    Schema.Required("length", Schema.Integer(), "Length of the returned slice"),
                    Schema.Required("totalLength", Schema.Integer(), "Total payload length"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more characters follow"),
                    Schema.Required("payload", Schema.Text(), "The JSON text slice")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => GetOutput(arguments)));
        }

        private ToolResult Status()
        {
            var status = context.Status();
            var process = context.Host.GetAttachedProcess();
            var project = context.Host.GetProjectSummary();

            var processJson = new JObject { ["attached"] = process.IsAttached };
            if (process.IsAttached)
            {
                processJson["valid"] = process.IsValid;
                processJson["id"] = process.Id;
                processJson["name"] = process.Name;
                processJson["path"] = process.Path;
                processJson["moduleCount"] = process.ModuleCount;
                processJson["sectionCount"] = process.SectionCount;
            }

            var structured = new JObject
            {
                ["server"] = new JObject
                {
                    ["name"] = status.ServerName,
                    ["pluginVersion"] = PluginVersion.Value,
                    ["endpoint"] = status.Url,
                    ["running"] = status.IsRunning,
                    ["allowMutations"] = status.AllowMutations,
                    ["protocolVersion"] = status.NegotiatedVersion
                },
                ["host"] = new JObject
                {
                    ["application"] = "ReClass.NET",
                    ["version"] = context.Host.HostVersion,
                    ["platform"] = context.Host.Platform,
                    ["pointerSize"] = context.Host.PointerSize
                },
                ["process"] = processJson,
                ["project"] = new JObject
                {
                    ["path"] = project.Path,
                    ["classCount"] = project.ClassCount,
                    ["enumCount"] = project.EnumCount,
                    ["selectedClassUuid"] = project.SelectedClassUuid,
                    ["selectedClassName"] = project.SelectedClassName
                }
            };

            return ToolResult.Ok(structured);
        }

        private ToolResult GetOutput(ToolArguments arguments)
        {
            var id = arguments.String("outputId");
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 20000, OutputCache.MaxCharacters);

            if (!context.OutputCache.TryGet(id, out var cached))
            {
                throw new ToolException($"No cached output with id '{id}'", "Cached outputs are dropped after 32 newer entries; re-run the tool that produced it.");
            }

            if (offset > cached.Payload.Length)
            {
                throw new InvalidArgumentsException($"'offset' {offset} is past the end of the payload ({cached.Payload.Length} characters)");
            }

            var length = Math.Min(limit, cached.Payload.Length - offset);

            var structured = new JObject
            {
                ["outputId"] = id,
                ["offset"] = offset,
                ["length"] = length,
                ["totalLength"] = cached.Payload.Length,
                ["hasMore"] = offset + length < cached.Payload.Length,
                ["payload"] = cached.Payload.Substring(offset, length)
            };

            return ToolResult.Ok(structured);
        }
    }

    internal sealed class ServerStatus
    {
        public string ServerName { get; set; }

        public string Url { get; set; }

        public bool IsRunning { get; set; }

        public bool AllowMutations { get; set; }

        public string NegotiatedVersion { get; set; }
    }
}
