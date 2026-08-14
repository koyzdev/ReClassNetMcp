using System;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Configuration;
using ReClassNetMcp.Host;
using ReClassNetMcp.Protocol;

namespace ReClassNetMcp.Tools
{
    internal sealed class ToolContext
    {
        public IReClassHost Host { get; }

        public ProjectAccess Project { get; }

        public OutputCache OutputCache { get; }

        public ServerSettings Settings { get; }

        public Func<ServerStatus> Status { get; }

        public ToolContext(IReClassHost host, ProjectAccess project, OutputCache outputCache, ServerSettings settings, Func<ServerStatus> status)
        {
            Host = host;
            Project = project;
            OutputCache = outputCache;
            Settings = settings;
            Status = status;
        }

        public ReClassNET.Memory.RemoteProcess RequireProcess()
        {
            var process = Host.Process;

            //
            // Two checks and not one: the host keeps the RemoteProcess object around after
            // the target died, so UnderlayingProcess stays non null while IsValid has already
            // flipped. Dropping either one lets a dead process through into a read.
            //
            if (process?.UnderlayingProcess == null)
            {
                throw new ToolException(
                    "No process is attached",
                    "Call list_processes and then attach_process first.");
            }

            if (!process.IsValid)
            {
                throw new ToolException(
                    "The attached process is no longer valid",
                    "The target has exited; attach to a live process again.");
            }

            return process;
        }
    }
}
