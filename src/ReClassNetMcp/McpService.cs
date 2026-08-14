using System;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Configuration;
using ReClassNetMcp.Host;
using ReClassNetMcp.Protocol;
using ReClassNetMcp.Server;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp
{
    internal sealed class McpService : IDisposable
    {
        private readonly IReClassHost host;

        private readonly ServerSettings settings;

        private readonly InstanceRegistry registry = new InstanceRegistry();

        private readonly OutputCache outputCache = new OutputCache();

        private readonly ToolRegistry tools = new ToolRegistry();

        private readonly ResourceRegistry resources = new ResourceRegistry();

        private McpDispatcher dispatcher;

        private McpHttpServer server;

        public McpService(IReClassHost host, ServerSettings settings)
        {
            this.host = host;
            this.settings = settings;
        }

        public ServerSettings Settings => settings;

        public string ServerName => "reclass" + (host.PointerSize == 4 ? "-x86" : string.Empty);

        public string Url => server != null && server.IsRunning ? server.Url : null;

        public bool IsRunning => server != null && server.IsRunning;

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            var projectAccess = new ProjectAccess(host, host.Logger);
            var context = new ToolContext(host, projectAccess, outputCache, settings, GetStatus);

            new ServerTools(context).Register(tools);
            new ProcessTools(context).Register(tools);
            new MemoryTools(context).Register(tools);
            new ProjectTools(context).Register(tools);
            new NodeTools(context).Register(tools);
            new EnumTools(context).Register(tools);
            new CodeTools(context).Register(tools);
            new ScannerTools(context).Register(tools);
            new ResourceTools(context).Register(resources);

            dispatcher = new McpDispatcher(host, tools, resources, outputCache, () => settings.AllowMutations);

            //
            // Prune before publishing. A host that was killed left its instance file
            // behind, and a client that picks that one up dials a dead port. Publishing
            // strictly after server.Start() also means the file only ever advertises a
            // url that is already listening.
            //
            InstanceRegistry.PruneStale();

            server = new McpHttpServer(host, dispatcher, settings);
            server.Start();

            registry.Publish(server.Url, server.Port, host.Platform, host.HostVersion, settings.TokenFingerprint(), ServerName);

            host.Log(HostLogLevel.Information, $"mcp: listening on {server.Url} (mutations {(settings.AllowMutations ? "enabled" : "disabled")})");
        }

        public void Stop()
        {
            if (server != null)
            {
                server.Stop();
                server = null;
            }

            registry.Remove();
        }

        public void Dispose()
        {
            Stop();
        }

        private ServerStatus GetStatus()
        {
            return new ServerStatus
            {
                ServerName = ServerName,
                Url = Url,
                IsRunning = IsRunning,
                AllowMutations = settings.AllowMutations,
                NegotiatedVersion = dispatcher?.NegotiatedVersion ?? ProtocolVersions.Advertised
            };
        }
    }
}
