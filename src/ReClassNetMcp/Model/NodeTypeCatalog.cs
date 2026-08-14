using System;
using System.Collections.Generic;
using System.Linq;
using ReClassNET.Nodes;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Model
{
    internal sealed class NodeTypeEntry
    {
        public string Name { get; }

        public Type Type { get; }

        public string Category { get; }

        public int MemorySize { get; }

        public bool IsContainer { get; }

        public bool IsWrapper { get; }

        public NodeTypeEntry(string name, Type type, string category, int memorySize, bool isContainer, bool isWrapper)
        {
            Name = name;
            Type = type;
            Category = category;
            MemorySize = memorySize;
            IsContainer = isContainer;
            IsWrapper = isWrapper;
        }
    }

    internal static class NodeTypeCatalog
    {
        //
        // ClassNode and VirtualMethodNode are out because their GetUserInterfaceInfo
        // throws outright, so probing them would take the catalog down with it. The rest
        // are the Legacy shims from DataExchange, which only exist so that old project
        // files can still be read and are converted away on load; every one of them
        // throws from MemorySize too. Matching is on the simple type name, which also
        // drops the live ReClassNET.Nodes.ClassInstanceArrayNode because the legacy shim
        // shares its name.
        //
        private static readonly string[] Excluded =
        {
            "ClassNode",
            "VirtualMethodNode",
            "ClassInstanceArrayNode",
            "ClassPointerArrayNode",
            "ClassPointerNode",
            "CustomNode"
        };

        private static readonly Dictionary<string, NodeTypeEntry> entries = Build();

        public static IEnumerable<NodeTypeEntry> All => entries.Values;

        public static bool TryGet(string name, out NodeTypeEntry entry)
        {
            return entries.TryGetValue(name, out entry);
        }

        public static NodeTypeEntry Require(string name)
        {
            if (!TryGet(name, out var entry))
            {
                throw new ToolException(
                    $"Unknown node type '{name}'",
                    "Call list_node_types to see the available type names.");
            }

            return entry;
        }

        //
        // The host knows the node types twice over, once for the UI type list and once for
        // serialization, but one is internal and the other private and there is no
        // InternalsVisibleTo anywhere, so reflecting over the assembly is the only way in.
        // Initialize is deliberately skipped when probing: it is the create-time hook, and
        // for the class wrapper nodes it calls ClassNode.Create, which adds a class to the
        // live project through a static event. Building the catalog would otherwise litter
        // the user's project with a junk class per wrapper type. Anything that still throws
        // is dropped rather than allowed to take the whole catalog down.
        //
        private static Dictionary<string, NodeTypeEntry> Build()
        {
            var result = new Dictionary<string, NodeTypeEntry>(StringComparer.OrdinalIgnoreCase);

            var candidates = typeof(BaseNode).Assembly
                .GetTypes()
                .Where(type => !type.IsAbstract && typeof(BaseNode).IsAssignableFrom(type))
                .Where(type => type.GetConstructor(Type.EmptyTypes) != null)
                .Where(type => Array.IndexOf(Excluded, type.Name) < 0)
                .OrderBy(type => type.Name, StringComparer.Ordinal);

            foreach (var type in candidates)
            {
                BaseNode probe;

                try
                {
                    probe = BaseNode.CreateInstanceFromType(type, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (probe == null)
                {
                    continue;
                }

                int size;

                try
                {
                    size = probe.MemorySize;
                }
                catch (Exception)
                {
                    size = 0;
                }

                result[type.Name] = new NodeTypeEntry(
                    type.Name,
                    type,
                    Categorize(probe),
                    size,
                    probe is BaseContainerNode,
                    probe is BaseWrapperNode);
            }

            return result;
        }

        private static string Categorize(BaseNode node)
        {
            if (node is BaseHexNode)
            {
                return "hex";
            }

            if (node is BitFieldNode || node is BoolNode || node is EnumNode)
            {
                return "flags";
            }

            if (node is FloatNode || node is DoubleNode)
            {
                return "float";
            }

            if (node is BaseNumericNode)
            {
                return "integer";
            }

            if (node is BaseMatrixNode)
            {
                return "matrix";
            }

            if (node is BaseTextNode || node is BaseTextPtrNode)
            {
                return "text";
            }

            if (node is BaseFunctionNode)
            {
                return "function";
            }

            if (node is BaseWrapperNode)
            {
                return "wrapper";
            }

            if (node is BaseContainerNode)
            {
                return "container";
            }

            return "other";
        }
    }
}
