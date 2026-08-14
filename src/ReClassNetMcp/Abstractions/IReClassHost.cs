using System;
using ReClassNET.Logger;
using ReClassNET.Memory;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace ReClassNetMcp.Abstractions
{
    internal enum HostLogLevel
    {
        Information,
        Warning,
        Error
    }

    internal sealed class AttachedProcessInfo
    {
        public bool IsAttached { get; set; }

        public bool IsValid { get; set; }

        public long Id { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public int ModuleCount { get; set; }

        public int SectionCount { get; set; }
    }

    internal sealed class ProjectSummary
    {
        public string Path { get; set; }

        public int ClassCount { get; set; }

        public int EnumCount { get; set; }

        public string SelectedClassUuid { get; set; }

        public string SelectedClassName { get; set; }
    }

    internal interface IReClassHost
    {
        string HostVersion { get; }

        string Platform { get; }

        int PointerSize { get; }

        void Log(HostLogLevel level, string message);

        T OnUi<T>(Func<T> function);

        ILogger Logger { get; }

        void OnUi(Action action);

        RemoteProcess Process { get; }

        ReClassNetProject Project { get; }

        ClassNode SelectedClass { get; set; }

        void ReplaceProject(ReClassNetProject project);

        void AttachToProcess(ProcessInfo info);

        void DetachProcess();

        AttachedProcessInfo GetAttachedProcess();

        ProjectSummary GetProjectSummary();
    }
}
