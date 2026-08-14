using System;
using System.Collections.Generic;
using ReClassNET.Logger;
using ReClassNET.Memory;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNetMcp.Abstractions;

namespace ReClassNetMcp.Tests
{
    internal sealed class RecordingLogger : ILogger
    {
        private readonly List<string> entries = new List<string>();

        public IReadOnlyList<string> Entries => entries;

        public event NewLogEntryEventHandler NewLogEntry;

        public void Log(Exception ex)
        {
            entries.Add(LogLevel.Error + ": " + ex.Message);

            NewLogEntry?.Invoke(LogLevel.Error, ex.Message, ex);
        }

        public void Log(LogLevel level, string message)
        {
            entries.Add(level + ": " + message);

            NewLogEntry?.Invoke(level, message, null);
        }
    }

    internal sealed class FakeReClassHost : IReClassHost
    {
        private readonly List<string> messages = new List<string>();

        public IReadOnlyList<string> Messages => messages;

        public RecordingLogger Recorder { get; } = new RecordingLogger();

        public string HostVersion { get; set; } = "1.2";

        public string Platform { get; set; } = "x64";

        public int PointerSize { get; set; } = IntPtr.Size;

        public int UiCallCount { get; private set; }

        public int AttachCallCount { get; private set; }

        public int DetachCallCount { get; private set; }

        public int ReplaceProjectCallCount { get; private set; }

        public ProcessInfo AttachedTo { get; private set; }

        public ILogger Logger => Recorder;

        public RemoteProcess Process => null;

        public ReClassNetProject Project { get; private set; } = new ReClassNetProject();

        public ClassNode SelectedClass { get; set; }

        public void Log(HostLogLevel level, string message)
        {
            messages.Add(level + ": " + message);

            Recorder.Log(Translate(level), message);
        }

        public T OnUi<T>(Func<T> function)
        {
            UiCallCount++;

            return function();
        }

        public void OnUi(Action action)
        {
            UiCallCount++;

            action();
        }

        public void ReplaceProject(ReClassNetProject project)
        {
            ReplaceProjectCallCount++;

            Project = project;
        }

        public void AttachToProcess(ProcessInfo info)
        {
            AttachCallCount++;

            AttachedTo = info;
        }

        public void DetachProcess()
        {
            DetachCallCount++;

            AttachedTo = null;
        }

        public AttachedProcessInfo GetAttachedProcess()
        {
            if (AttachedTo == null)
            {
                return new AttachedProcessInfo { IsAttached = false };
            }

            return new AttachedProcessInfo
            {
                IsAttached = true,
                IsValid = true,
                Id = AttachedTo.Id.ToInt64(),
                Name = AttachedTo.Name,
                Path = AttachedTo.Path,
                ModuleCount = 0,
                SectionCount = 0
            };
        }

        public ProjectSummary GetProjectSummary()
        {
            return new ProjectSummary
            {
                Path = Project.Path,
                ClassCount = Project.Classes.Count,
                EnumCount = Project.Enums.Count,
                SelectedClassUuid = SelectedClass?.Uuid.ToString("D"),
                SelectedClassName = SelectedClass?.Name
            };
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
