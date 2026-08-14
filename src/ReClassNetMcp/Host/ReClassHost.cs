using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using ReClassNET;
using ReClassNET.Logger;
using ReClassNET.Memory;
using ReClassNET.Nodes;
using ReClassNET.Plugins;
using ReClassNET.Project;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Host
{
    internal sealed class ReClassHost : IReClassHost
    {
        private readonly IPluginHost pluginHost;

        public ReClassHost(IPluginHost pluginHost)
        {
            this.pluginHost = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
        }

        public string HostVersion => Constants.ApplicationVersion;

        public string Platform => Constants.Platform;

        public int PointerSize => IntPtr.Size;

        public ILogger Logger => pluginHost.Logger;

        public RemoteProcess Process => pluginHost.Process;

        public ReClassNetProject Project => pluginHost.MainWindow.CurrentProject;

        public ClassNode SelectedClass
        {
            get => pluginHost.MainWindow.CurrentClassNode;
            set => pluginHost.MainWindow.CurrentClassNode = value;
        }

        public void Log(HostLogLevel level, string message)
        {
            pluginHost.Logger.Log(Translate(level), message);
        }

        //
        // Every project and node access goes through here, reads included and not just
        // writes. Nothing in the host model is locked: ReClassNetProject.classes and
        // BaseContainerNode.nodes are plain lists that the WinForms render loop walks
        // while we are in them, so enumerating off-thread is as unsafe as mutating.
        // Memory reads, scanning, pattern scanning and disassembly are background safe
        // by design and are deliberately not routed through here.
        //
        public T OnUi<T>(Func<T> function)
        {
            var form = pluginHost.MainWindow;
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
            {
                throw new ToolException("The ReClass.NET main window is not available");
            }

            if (!form.InvokeRequired)
            {
                return function();
            }

            var result = default(T);
            Exception failure = null;

            form.Invoke((MethodInvoker)(() =>
            {
                try
                {
                    result = function();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }));

            //
            // Rethrow through ExceptionDispatchInfo so a ToolException raised inside the
            // invoked delegate arrives at the caller as itself. Letting Control.Invoke
            // unwind it would wrap it in a TargetInvocationException and the dispatcher
            // would report a generic internal error instead of the real message.
            //
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result;
        }

        public void OnUi(Action action)
        {
            OnUi<object>(() =>
            {
                action();
                return null;
            });
        }

        public void AttachToProcess(ProcessInfo info)
        {
            OnUi(() => pluginHost.MainWindow.AttachToProcess(info));
        }

        public void DetachProcess()
        {
            OnUi(() => pluginHost.Process.Close());
        }

        public void ReplaceProject(ReClassNetProject project)
        {
            OnUi(() => pluginHost.MainWindow.SetProject(project));
        }

        //
        // Not marshalled on purpose. RemoteProcess reads, IsValid and the Modules and
        // Sections snapshots are all background safe, so an attach query does not have
        // to fight the UI thread for a turn.
        //
        public AttachedProcessInfo GetAttachedProcess()
        {
            var process = pluginHost.Process;
            var info = process.UnderlayingProcess;

            if (info == null)
            {
                return new AttachedProcessInfo { IsAttached = false };
            }

            return new AttachedProcessInfo
            {
                IsAttached = true,
                IsValid = process.IsValid,
                Id = info.Id.ToInt64(),
                Name = info.Name,
                Path = info.Path,
                ModuleCount = process.Modules.Count(),
                SectionCount = process.Sections.Count()
            };
        }

        public ProjectSummary GetProjectSummary()
        {
            return OnUi(() =>
            {
                var project = pluginHost.MainWindow.CurrentProject;
                if (project == null)
                {
                    return new ProjectSummary();
                }

                var selected = pluginHost.MainWindow.CurrentClassNode;

                return new ProjectSummary
                {
                    Path = project.Path,
                    ClassCount = project.Classes.Count,
                    EnumCount = project.Enums.Count,
                    SelectedClassUuid = selected?.Uuid.ToString("D"),
                    SelectedClassName = selected?.Name
                };
            });
        }

        private static LogLevel Translate(HostLogLevel level)
        {
            switch (level)
            {
                case HostLogLevel.Warning:
                    return LogLevel.Warning;

                case HostLogLevel.Error:
                    return LogLevel.Error;

                default:
                    return LogLevel.Information;
            }
        }
    }
}
