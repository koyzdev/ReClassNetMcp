using System;
using System.IO;
using System.Reflection;
using ReClassNET.Plugins;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Configuration;
using ReClassNetMcp.Host;
using ReClassNetMcp.Install;
using ReClassNetMcp.Ui;

namespace ReClassNetMcp
{
    //
    // The loader never looks at our code to decide whether to load us. It reads the
    // Win32 version info: ProductName has to be exactly "ReClass.NET Plugin" or the
    // file is skipped, and a null ProductName gets us treated as a native plugin.
    // The entry type is then resolved by name as <FileName>.<FileName>Ext, so the
    // assembly file name is load bearing and has to stay a legal identifier.
    //
    public class ReClassNetMcpExt : Plugin
    {
        private static readonly string PluginDirectory = ResolvePluginDirectory();

        private McpService service;

        private McpMenu menu;

        private IReClassHost host;

        static ReClassNetMcpExt()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSideBySideAssembly;
        }

        public override bool Initialize(IPluginHost pluginHost)
        {
            if (pluginHost == null)
            {
                return false;
            }

            //
            // Plugins are loaded before the first SetProject call, so
            // MainForm.CurrentProject is still null for the whole of this method.
            // Nothing here may reach for the project.
            //
            host = new ReClassHost(pluginHost);

            try
            {
                var settings = ServerSettings.Load();

                service = new McpService(host, settings);

                if (settings.Enabled)
                {
                    service.Start();
                }
                else
                {
                    host.Log(HostLogLevel.Information, "mcp: server disabled by configuration");
                }

                AttachMenu(pluginHost);
            }
            catch (Exception ex)
            {
                host.Log(HostLogLevel.Error, $"mcp: failed to start: {ex.Message}");
            }

            return true;
        }

        public override void Terminate()
        {
            menu?.Detach();
            menu = null;

            service?.Dispose();
            service = null;
            host = null;
        }

        private void AttachMenu(IPluginHost pluginHost)
        {
            var menuStrip = pluginHost.MainWindow?.MainMenu;
            if (menuStrip == null)
            {
                return;
            }

            menu = new McpMenu(
                menuStrip,
                () => service != null && service.IsRunning,
                () => service?.Url,
                () => service?.Settings.Token,
                () => service?.ServerName,
                StartService,
                StopService,
                () => service != null && service.Settings.AllowMutations,
                allow => SetAllowMutations(allow),
                Install,
                DescribeEntry);

            menu.Attach();
        }

        private void StartService()
        {
            if (service == null)
            {
                return;
            }

            service.Start();
            service.Settings.Enabled = true;
            service.Settings.Save();
        }

        private void StopService()
        {
            if (service == null)
            {
                return;
            }

            service.Stop();
            service.Settings.Enabled = false;
            service.Settings.Save();
        }

        private void SetAllowMutations(bool allow)
        {
            if (service == null)
            {
                return;
            }

            service.Settings.AllowMutations = allow;
            service.Settings.Save();
        }

        private InstallResult Install(InstallTarget target, string projectDirectory)
        {
            return CreateInstaller().Install(target, projectDirectory);
        }

        private string DescribeEntry()
        {
            return CreateInstaller().DescribeEntry();
        }

        private ClientInstaller CreateInstaller()
        {
            if (service == null || !service.IsRunning)
            {
                throw new InvalidOperationException("The MCP server is not running, enable it first.");
            }

            return new ClientInstaller(service.ServerName, service.Url, service.Settings.Token);
        }

        private static string ResolvePluginDirectory()
        {
            try
            {
                var location = new Uri(typeof(ReClassNetMcpExt).Assembly.CodeBase).LocalPath;
                return Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        //
        // .NET Framework only ever reads the AppDomain's config, so a plugin cannot
        // ship binding redirects of its own; a .dll.config next to us is ignored. The
        // static constructor hooks this up before the first dependency load. We only
        // answer for files sitting in our own folder and return null for everything
        // else, so the host's own resolution is left alone.
        //
        private static Assembly ResolveSideBySideAssembly(object sender, ResolveEventArgs args)
        {
            if (PluginDirectory == null)
            {
                return null;
            }

            var candidate = Path.Combine(PluginDirectory, new AssemblyName(args.Name).Name + ".dll");
            if (!File.Exists(candidate))
            {
                return null;
            }

            return Assembly.LoadFrom(candidate);
        }
    }
}
