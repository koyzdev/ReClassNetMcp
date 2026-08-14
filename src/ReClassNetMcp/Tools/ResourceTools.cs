using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.Nodes;
using ReClassNetMcp.Model;
using ReClassNetMcp.Protocol;

namespace ReClassNetMcp.Tools
{
    internal sealed class ResourceTools
    {
        private const int MaximumDepth = 3;

        private readonly ToolContext context;

        public ResourceTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ResourceRegistry registry)
        {
            registry.Add(new ResourceDefinition(
                "reclass://project",
                "project",
                "Open project",
                "The project currently open in ReClass.NET: file path, class count, enum count, the class selected in the ui and a compact list of every class with its uuid, name, address formula, memory size in bytes and direct child node count. Read reclass://class/<uuid> for the node tree of one class.",
                (argument, token) => ReadProject()));

            registry.Add(new ResourceDefinition(
                "reclass://process",
                "process",
                "Attached process",
                "The process ReClass.NET is attached to: id, name, image path, module count and section count, plus the host platform and pointer size in bytes. Reports attached=false when no process is attached; call the attach_process tool to attach one.",
                (argument, token) => ReadProcess()));

            registry.Add(new ResourceDefinition(
                "reclass://modules",
                "modules",
                "Loaded modules",
                "Every module loaded in the attached process as name, image path, start address, end address and size in bytes, ordered by start address. Addresses are lowercase hex strings. Reports attached=false when no process is attached; call the attach_process tool first.",
                (argument, token) => ReadModules(token)));

            registry.Add(new ResourceDefinition(
                "reclass://sections",
                "sections",
                "Memory sections",
                "Every memory section of the attached process as name, start address, end address, size in bytes, category (Unknown, CODE, DATA, HEAP), type (Private, Mapped, Image), protection flags and owning module name, ordered by start address. Reports attached=false when no process is attached; call the attach_process tool first.",
                (argument, token) => ReadSections(token)));

            registry.Add(new ResourceDefinition(
                "reclass://node-types",
                "node-types",
                "Node types",
                "Every node type that can be placed into a class, as name, category, default memory size in bytes, whether it holds child nodes and whether it wraps an inner node. These names are the values accepted by the add_node, insert_node and change_node_type tools.",
                (argument, token) => ReadNodeTypes()));

            registry.Add(new ResourceDefinition(
                "reclass://class/{uuid}",
                "class",
                "Class layout",
                "One class identified by its uuid, with its address formula, memory size in bytes, comment and its node tree three levels deep. Every node carries the handle to pass to the node tools, its index in the parent, its offset from the class start as hex, its type name, name, comment and memory size in bytes. Get the uuids from reclass://project or the list_classes tool.",
                (argument, token) => ReadClass(argument)));
        }

        private JObject ReadProject()
        {
            return context.Project.Read(project =>
            {
                var selected = context.Host.SelectedClass;

                var classes = new JArray();

                foreach (var node in project.Classes)
                {
                    classes.Add(new JObject
                    {
                        ["uuid"] = node.Uuid.ToString("D"),
                        ["name"] = node.Name,
                        ["addressFormula"] = node.AddressFormula,
                        ["memorySize"] = node.MemorySize,
                        ["nodeCount"] = node.Nodes.Count
                    });
                }

                return new JObject
                {
                    ["path"] = project.Path,
                    ["classCount"] = project.Classes.Count,
                    ["enumCount"] = project.Enums.Count,
                    ["selectedClassUuid"] = selected?.Uuid.ToString("D"),
                    ["selectedClassName"] = selected?.Name,
                    ["classes"] = classes
                };
            });
        }

        private JObject ReadProcess()
        {
            var info = context.Host.GetAttachedProcess();

            var payload = new JObject
            {
                ["attached"] = info.IsAttached,
                ["platform"] = context.Host.Platform,
                ["pointerSize"] = context.Host.PointerSize
            };

            if (info.IsAttached)
            {
                payload["valid"] = info.IsValid;
                payload["id"] = info.Id;
                payload["name"] = info.Name;
                payload["path"] = info.Path;
                payload["moduleCount"] = info.ModuleCount;
                payload["sectionCount"] = info.SectionCount;
            }

            return payload;
        }

        private JObject ReadModules(CancellationToken token)
        {
            //
            // A resource read has to answer, not fail. Clients fetch every resource they know
            // about without being asked to, so a missing process is part of the payload here
            // instead of the error that RequireProcess would raise on its own.
            //
            if (!IsAttached())
            {
                return NotAttached();
            }

            var modules = context.RequireProcess().Modules
                .OrderBy(module => module.Start.ToInt64())
                .ToList();

            var items = new JArray();

            foreach (var module in modules)
            {
                token.ThrowIfCancellationRequested();

                items.Add(new JObject
                {
                    ["name"] = module.Name,
                    ["path"] = module.Path,
                    ["start"] = Format.Hex(module.Start),
                    ["end"] = Format.Hex(module.End),
                    ["size"] = module.Size.ToInt64()
                });
            }

            return new JObject
            {
                ["attached"] = true,
                ["count"] = items.Count,
                ["modules"] = items
            };
        }

        private JObject ReadSections(CancellationToken token)
        {
            if (!IsAttached())
            {
                return NotAttached();
            }

            var sections = context.RequireProcess().Sections
                .OrderBy(section => section.Start.ToInt64())
                .ToList();

            var items = new JArray();

            foreach (var section in sections)
            {
                token.ThrowIfCancellationRequested();

                items.Add(new JObject
                {
                    ["name"] = section.Name,
                    ["start"] = Format.Hex(section.Start),
                    ["end"] = Format.Hex(section.End),
                    ["size"] = section.Size.ToInt64(),
                    ["category"] = section.Category.ToString(),
                    ["type"] = section.Type.ToString(),
                    ["protection"] = section.Protection.ToString(),
                    ["moduleName"] = section.ModuleName
                });
            }

            return new JObject
            {
                ["attached"] = true,
                ["count"] = items.Count,
                ["sections"] = items
            };
        }

        private JObject ReadNodeTypes()
        {
            var items = new JArray();

            foreach (var entry in NodeTypeCatalog.All.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                items.Add(new JObject
                {
                    ["name"] = entry.Name,
                    ["category"] = entry.Category,
                    ["memorySize"] = entry.MemorySize,
                    ["isContainer"] = entry.IsContainer,
                    ["isWrapper"] = entry.IsWrapper
                });
            }

            return new JObject
            {
                ["count"] = items.Count,
                ["types"] = items
            };
        }

        private JObject ReadClass(string argument)
        {
            if (string.IsNullOrEmpty(argument) || !Guid.TryParse(argument, out var uuid))
            {
                throw new InvalidArgumentsException($"'{argument}' is not a class uuid; the uri must be 'reclass://class/<uuid>'");
            }

            return context.Project.Read(project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["name"] = node.Name,
                    ["addressFormula"] = node.AddressFormula,
                    ["memorySize"] = node.MemorySize,
                    ["comment"] = node.Comment,
                    ["nodes"] = Children(node, node, 1)
                };
            });
        }

        private static JArray Children(ClassNode owner, BaseNode parent, int depth)
        {
            var items = new JArray();
            var index = 0;

            foreach (var child in Enumerate(parent))
            {
                items.Add(Describe(owner, child, index, depth));

                ++index;
            }

            return items;
        }

        private static JObject Describe(ClassNode owner, BaseNode node, int index, int depth)
        {
            var payload = new JObject
            {
                ["handle"] = NodeHandle.Format(owner, node),
                ["index"] = index,
                ["offset"] = Format.Hex(node.Offset),
                ["type"] = node.GetType().Name,
                ["name"] = node.Name,
                ["comment"] = node.Comment,
                ["memorySize"] = node.MemorySize
            };

            //
            // The walk stops at a class and hands over the uuid instead of descending into it.
            // The class graph is cyclic as soon as a structure points back at its parent, and a
            // resource has no paging to escape with, which is also why MaximumDepth is fixed.
            //
            if (node is ClassNode reference)
            {
                payload["classUuid"] = reference.Uuid.ToString("D");

                return payload;
            }

            if (depth >= MaximumDepth)
            {
                return payload;
            }

            var children = Children(owner, node, depth + 1);
            if (children.Count > 0)
            {
                payload["children"] = children;
            }

            return payload;
        }

        private static IEnumerable<BaseNode> Enumerate(BaseNode node)
        {
            if (node is BaseContainerNode container)
            {
                return container.Nodes;
            }

            if (node is BaseWrapperNode wrapper && wrapper.InnerNode != null)
            {
                return new[] { wrapper.InnerNode };
            }

            return Array.Empty<BaseNode>();
        }

        private bool IsAttached()
        {
            var process = context.Host.Process;

            return process?.UnderlayingProcess != null && process.IsValid;
        }

        private static JObject NotAttached()
        {
            return new JObject
            {
                ["attached"] = false,
                ["hint"] = "No live process is attached; call the list_processes and attach_process tools first."
            };
        }
    }
}
