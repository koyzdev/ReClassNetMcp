using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Configuration
{
    //
    // How a client finds a running server. One file per pid rather than one shared file,
    // because an x86 and an x64 ReClass.NET are separate installs that people genuinely
    // run at the same time and neither should be able to overwrite the other's entry.
    // Every write goes through AtomicFile so a reader never catches a half-written file.
    // Nothing removes these on a hard kill, so the pid in the file is also the liveness
    // marker and PruneStale sweeps the ones whose process is gone.
    //
    internal sealed class InstanceRegistry
    {
        private readonly string path;

        public InstanceRegistry()
        {
            path = Path.Combine(ServerSettings.Directory, $"instance_{Process.GetCurrentProcess().Id}.json");
        }

        public void Publish(string url, int port, string platform, string hostVersion, string tokenFingerprint, string serverName)
        {
            var document = new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["port"] = port,
                ["url"] = url,
                ["platform"] = platform,
                ["hostVersion"] = hostVersion,
                ["pluginVersion"] = PluginVersion.Value,
                ["serverName"] = serverName,
                ["tokenFingerprint"] = tokenFingerprint,
                ["startedAt"] = DateTime.UtcNow.ToString("o")
            };

            AtomicFile.Write(path, document.ToString(Formatting.Indented));
        }

        public void Remove()
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static void PruneStale()
        {
            try
            {
                if (!Directory.Exists(ServerSettings.Directory))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(ServerSettings.Directory, "instance_*.json"))
                {
                    if (!IsAlive(file))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool IsAlive(string file)
        {
            int pid;

            try
            {
                var document = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                pid = document.Value<int?>("pid") ?? 0;
            }
            catch (Exception)
            {
                return false;
            }

            if (pid <= 0)
            {
                return false;
            }

            if (pid == Process.GetCurrentProcess().Id)
            {
                return true;
            }

            //
            // GetProcessById signals a dead pid by throwing ArgumentException, so the
            // catch is the answer here and not an error path. Erring towards false is
            // deliberate: pruning a file we could not make sense of only costs a client
            // one rediscovery, whereas keeping a stale one hands out a dead port.
            //
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
