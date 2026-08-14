using System;
using System.Collections.Generic;
using System.IO;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Logger;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Model;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Host
{
    internal sealed class ProjectAccess
    {
        private readonly IReClassHost host;

        private readonly ILogger logger;

        private readonly SnapshotRing snapshots = new SnapshotRing();

        public ProjectAccess(IReClassHost host, ILogger logger)
        {
            this.host = host;
            this.logger = logger;
        }

        public SnapshotRing Snapshots => snapshots;

        public T Read<T>(Func<ReClassNetProject, T> action)
        {
            return host.OnUi(() => action(Require()));
        }

        //
        // ReClass.NET has no undo stack and no dirty flag at all, so an agent editing a
        // tree over the wire has nothing to fall back on. We take a full serialised copy
        // of the project before every mutation and keep it in the ring. Snapshot and
        // mutation share one UI turn so the two can never disagree.
        //
        public T Mutate<T>(string reason, Func<ReClassNetProject, T> action)
        {
            return host.OnUi(() =>
            {
                var project = Require();

                snapshots.Push(reason, Serialize(project), project.Classes.Count);

                return action(project);
            });
        }

        public bool Undo()
        {
            if (!snapshots.TryPop(out var snapshot))
            {
                return false;
            }

            //
            // Restoring means loading the snapshot into a fresh project and swapping the
            // whole instance in, because there is no host API to roll a change back. The
            // saved file does not carry its own location, so we copy the live path over by
            // hand; without that an undo would leave the project looking like one that had
            // never been saved and save_project would refuse it without an explicit path.
            //
            var restored = new ReClassNetProject();

            using (var stream = new MemoryStream(snapshot.Payload, false))
            {
                new ReClassNetFile(restored).Load(stream, logger);
            }

            var currentPath = host.OnUi(() => Require().Path);
            restored.Path = currentPath;

            host.ReplaceProject(restored);
            return true;
        }

        public byte[] Serialize(ReClassNetProject project)
        {
            using (var stream = new MemoryStream())
            {
                new ReClassNetFile(project).Save(stream, logger);
                return stream.ToArray();
            }
        }

        public ClassNode RequireClass(ReClassNetProject project, Guid uuid)
        {
            if (!project.ContainsClass(uuid))
            {
                throw new ToolException(
                    $"No class with uuid {uuid:D}",
                    "Call list_classes to get the current class uuids.");
            }

            return project.GetClassByUuid(uuid);
        }

        public BaseNode Resolve(ReClassNetProject project, NodeHandle handle)
        {
            var owner = RequireClass(project, handle.ClassUuid);

            BaseNode current = owner;

            for (var i = 0; i < handle.Path.Count; ++i)
            {
                var index = handle.Path[i];

                if (current is BaseContainerNode container)
                {
                    if (index >= container.Nodes.Count)
                    {
                        throw new ToolException(
                            $"Node handle '{handle}' is out of range at position {i}: the container holds {container.Nodes.Count} children",
                            "Index paths shift after every insert or remove; re-read the class with get_class.");
                    }

                    current = container.Nodes[index];
                    continue;
                }

                if (current is BaseWrapperNode wrapper)
                {
                    if (index != 0 || wrapper.InnerNode == null)
                    {
                        throw new ToolException(
                            $"Node handle '{handle}' is invalid at position {i}: a wrapper node has a single inner node at index 0",
                            null);
                    }

                    current = wrapper.InnerNode;
                    continue;
                }

                throw new ToolException(
                    $"Node handle '{handle}' is invalid at position {i}: '{current.GetType().Name}' has no children",
                    null);
            }

            return current;
        }

        public BaseContainerNode ResolveContainer(ReClassNetProject project, NodeHandle handle)
        {
            var node = Resolve(project, handle);

            if (node is BaseContainerNode container)
            {
                return container;
            }

            throw new ToolException(
                $"Node '{handle}' is a '{node.GetType().Name}', which cannot hold child nodes",
                "Pass a class, union or vtable handle.");
        }

        public ClassNode OwnerOf(BaseNode node)
        {
            if (node is ClassNode owner)
            {
                return owner;
            }

            return node.GetParentClass();
        }

        private ReClassNetProject Require()
        {
            var project = host.Project;
            if (project == null)
            {
                throw new ToolException("No project is open yet", "Wait until ReClass.NET has finished starting up.");
            }

            return project;
        }
    }
}
