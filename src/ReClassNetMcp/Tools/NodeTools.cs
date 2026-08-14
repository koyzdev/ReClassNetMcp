using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.AddressParser;
using ReClassNET.Memory;
using ReClassNET.Nodes;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class NodeTools
    {
        private const int MaxBatch = 256;

        private const int MaxBytes = 65536;

        private const int MaxTextLength = 4096;

        private const int MaxArrayCount = 65536;

        private const int MaxBits = 64;

        private const int DefaultListLimit = 200;

        private const int MaxListLimit = 1000;

        private readonly ToolContext context;

        public NodeTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "list_node_types",
                "List node types",
                "List every node type that add_node, insert_node, change_node_type and set_wrapped_type accept, with its default size in bytes and whether it holds children or wraps an inner node. ClassNode, VirtualMethodNode and the legacy ClassPointer/ClassInstanceArray/ClassPointerArray shims are intentionally absent: a class is created with create_class and referenced with a ClassInstanceNode plus set_class_reference, and vtable slots are created by calling add_bytes on a VirtualMethodTableNode.",
                Schema.Object(
                    Schema.Optional("category", Schema.Enum("hex", "integer", "float", "flags", "matrix", "text", "function", "wrapper", "container", "other"), "Only return types of this category"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, MaxListLimit), "Items to return, default 200")),
                Schema.Object(
                    Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Types as {name, category, memorySize, isContainer, isWrapper}, sorted by category then name"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Matching type count"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListNodeTypes(arguments)));

            registry.Add(new ToolDefinition(
                "add_node",
                "Add nodes",
                "Append one or more typed nodes to the end of a container. 'parent' is a class uuid, or the handle of a union or vtable node. Pass the whole field list of a structure in one call: the batch is applied as a single undoable change and is far cheaper than one call per field. Use insert_node when the node must land in the middle instead of at the end.",
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(EntrySchema("parent", "Handle of the container that receives the node: a class uuid, or a union or vtable node handle"), MaxBatch), "The nodes to append, at most 256; a single object is also accepted")),
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.AnyObject()), "Created nodes as {handle, type, name, comment, offset, size}, with the handles valid after the mutation"),
                    Schema.Required("count", Schema.Integer(), "Number of created nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => AddNodes(arguments, token)));

            registry.Add(new ToolDefinition(
                "insert_node",
                "Insert nodes",
                "Insert one or more typed nodes directly in front of an existing node. 'before' is the handle of the node that will follow the new one; its container receives the insert. Inserting grows the class, it does not overwrite the node you insert before. To reuse existing padding instead, call change_node_type on the hex node that already covers those bytes.",
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(EntrySchema("before", "Handle of the node the new node is inserted in front of; its parent container receives the insert"), MaxBatch), "The nodes to insert, at most 256; a single object is also accepted")),
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.AnyObject()), "Created nodes as {handle, type, name, comment, offset, size}, with the handles valid after the mutation"),
                    Schema.Required("count", Schema.Integer(), "Number of created nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => InsertNodes(arguments, token)));

            registry.Add(new ToolDefinition(
                "delete_node",
                "Delete nodes",
                "Remove nodes from their container. Removal shrinks the class and does not backfill the freed bytes, so every following field moves down by the removed size; call add_bytes or insert_bytes afterwards if the layout must keep its total size. Index paths shift, so use the container handles returned here to re-read the class.",
                Schema.Object(
                    Schema.Required("handles", Schema.ArrayOf(Schema.Text(), MaxBatch), "Node handles to remove, at most 256; a single string is also accepted")),
                Schema.Object(
                    Schema.Required("deleted", Schema.Integer(), "Number of nodes actually removed"),
                    Schema.Required("handles", Schema.ArrayOf(Schema.AnyObject()), "The affected containers after the mutation as {handle, type, name, comment, offset, size}")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => DeleteNodes(arguments, token)));

            registry.Add(new ToolDefinition(
                "change_node_type",
                "Change node types",
                "Retype existing nodes in place, which is the normal way to turn hex padding into real fields. ReClass.NET only compensates a size change when the replacement is SMALLER: the freed bytes come back as hex nodes. A LARGER replacement silently overlaps the nodes that follow, so those entries report a 'warning' with the number of absorbed bytes. Replacing with a text node additionally drops the old name and comment, because BaseTextNode copies only the byte length; set them again with set_node_name and set_node_comment.",
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.Object(
                        Schema.Required("handle", Schema.Text(), "Handle of the node to retype"),
                        Schema.Required("type", Schema.Text(), "New node type name from list_node_types")), MaxBatch), "The nodes to retype, at most 256; a single object is also accepted")),
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.AnyObject()), "New nodes as {handle, type, name, comment, offset, size, previousType, previousSize} plus 'warning' when the node grew"),
                    Schema.Required("count", Schema.Integer(), "Number of retyped nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => ChangeNodeTypes(arguments, token)));

            registry.Add(new ToolDefinition(
                "set_node_name",
                "Set node names",
                "Set the field name of one or more nodes. The name is what generate_code emits as the member identifier, so it should be a valid C++ identifier. Names are free-form and duplicates are legal, they are not identities: keep using handles to address nodes.",
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.Object(
                        Schema.Required("handle", Schema.Text(), "Handle of the node to rename"),
                        Schema.Required("name", Schema.Text(), "New field name")), MaxBatch), "The renames, at most 256; a single object is also accepted")),
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.AnyObject()), "Renamed nodes as {handle, type, name, comment, offset, size}"),
                    Schema.Required("count", Schema.Integer(), "Number of renamed nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => ApplyText(arguments, token, true)));

            registry.Add(new ToolDefinition(
                "set_node_comment",
                "Set node comments",
                "Set the trailing comment of one or more nodes. Pass an empty string to clear a comment. Comments survive save and load and are the cheapest place to record what a field means while you are still guessing.",
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.Object(
                        Schema.Required("handle", Schema.Text(), "Handle of the node to annotate"),
                        Schema.Required("comment", Schema.Text(), "New comment, empty string clears it")), MaxBatch), "The comments, at most 256; a single object is also accepted")),
                Schema.Object(
                    Schema.Required("nodes", Schema.ArrayOf(Schema.AnyObject()), "Annotated nodes as {handle, type, name, comment, offset, size}"),
                    Schema.Required("count", Schema.Integer(), "Number of annotated nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => ApplyText(arguments, token, false)));

            registry.Add(new ToolDefinition(
                "set_node_size",
                "Resize a node",
                "Resize a node that carries its own size. Pass exactly one of 'length' for a text node (the character count), 'count' for an array node (the element count, the current index is clamped into the new range) or 'bits' for a BitFieldNode (snapped to 8, 16, 32 or 64). Every other node type has a fixed size, use change_node_type on it instead. Unlike change_node_type this never overwrites a neighbour: every following field is shifted and the class grows or shrinks by the difference, so the offsets you already recorded move.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the node to resize"),
                    Schema.Optional("length", Schema.Integer(1, MaxTextLength), "Character count, text node types only"),
                    Schema.Optional("count", Schema.Integer(1, MaxArrayCount), "Element count, array node types only"),
                    Schema.Optional("bits", Schema.Integer(1, MaxBits), "Bit width, BitFieldNode only, snapped to 8, 16, 32 or 64")),
                Schema.Object(
                    Schema.Required("node", Schema.AnyObject(), "The node after the resize as {handle, type, name, comment, offset, size}"),
                    Schema.Required("previousSize", Schema.Integer(), "Size in bytes before the resize")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetNodeSize(arguments)));

            registry.Add(new ToolDefinition(
                "set_wrapped_type",
                "Set wrapped type",
                "Set what a pointer or array node wraps. 'handle' must point at a wrapper node and 'type' is the new inner type from list_node_types. To model an array of a class or a pointer to a class, wrap a ClassInstanceNode here and then call set_class_reference on the returned inner handle. Pointers and arrays reject a bare class, which is why the ClassInstanceNode step exists.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the pointer or array node"),
                    Schema.Required("type", Schema.Text(), "New inner node type name from list_node_types")),
                Schema.Object(
                    Schema.Required("wrapper", Schema.AnyObject(), "The wrapper after the change as {handle, type, name, comment, offset, size}"),
                    Schema.Required("inner", Schema.AnyObject(), "The new inner node, address it with this handle")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetWrappedType(arguments)));

            registry.Add(new ToolDefinition(
                "set_class_reference",
                "Point a node at a class",
                "Point a ClassInstanceNode at an existing class, which is how one structure embeds or references another. 'handle' must be a class instance node, 'uuid' the target class from list_classes. The wiring is refused when it would make the target class reachable from its own parent, because ReClass.NET would then recurse while drawing; put a PointerNode in front of the instance to model a self referencing structure such as a linked list. The class the node referenced before stays in the project, remove_unused_classes cleans it up.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the class instance node"),
                    Schema.Required("uuid", Schema.Uuid(), "Uuid of the class to reference")),
                Schema.Object(
                    Schema.Required("node", Schema.AnyObject(), "The node after the wiring as {handle, type, name, comment, offset, size}"),
                    Schema.Required("referencedClass", Schema.AnyObject(), "The referenced class as {uuid, name, size}"),
                    Schema.Required("previousClassUuid", Schema.Text(), "Uuid of the class that was referenced before, or empty")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetClassReference(arguments)));

            registry.Add(new ToolDefinition(
                "add_bytes",
                "Append bytes",
                "Append 'size' bytes of hex padding to the end of a container, which is how you make room before typing fields. On a VirtualMethodTableNode the same call appends virtual method slots instead, one per pointer size, because that is the only child type a vtable accepts.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the container: a class uuid, or a union or vtable node handle"),
                    Schema.Required("size", Schema.Integer(1, MaxBytes), "Bytes to append")),
                Schema.Object(
                    Schema.Required("container", Schema.AnyObject(), "The container after the change as {handle, type, name, comment, offset, size}"),
                    Schema.Required("added", Schema.ArrayOf(Schema.AnyObject()), "The appended nodes"),
                    Schema.Required("count", Schema.Integer(), "Number of appended nodes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => AddBytes(arguments)));

            registry.Add(new ToolDefinition(
                "insert_bytes",
                "Insert bytes",
                "Insert 'size' bytes of hex padding directly in front of an existing node. Use this to open a gap in the middle of a class, for example after discovering that a structure starts earlier than assumed. Everything from the given node onwards moves up by 'size' bytes.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the node the bytes are inserted in front of"),
                    Schema.Required("size", Schema.Integer(1, MaxBytes), "Bytes to insert")),
                Schema.Object(
                    Schema.Required("container", Schema.AnyObject(), "The container after the change as {handle, type, name, comment, offset, size}"),
                    Schema.Required("inserted", Schema.ArrayOf(Schema.AnyObject()), "The inserted nodes"),
                    Schema.Required("count", Schema.Integer(), "Number of inserted nodes"),
                    Schema.Required("position", Schema.AnyObject(), "The node the bytes were inserted in front of, with its handle after the mutation")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => InsertBytes(arguments)));

            registry.Add(new ToolDefinition(
                "suggest_types",
                "Suggest node types",
                "Run the ReClass.NET dissector over the hex nodes of a class and report what each one looks like in the attached process, WITHOUT changing anything. Requires an attached process and a class address that resolves to readable memory. Only nodes that produced a guess are listed, so an empty result means the bytes look like nothing in particular. Apply the guesses with dissect_nodes, or type individual fields yourself with change_node_type.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Uuid of the class to analyse"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, MaxListLimit), "Items to return, default 200")),
                Schema.Object(
                    Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Suggestions as {handle, offset, currentType, suggestedType}"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Number of nodes that produced a guess"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => SuggestTypes(arguments, token)));

            registry.Add(new ToolDefinition(
                "dissect_nodes",
                "Dissect hex nodes",
                "Apply the dissector guesses: every hex node that looks like a pointer, a float, an integer or a string is replaced by that type in the live project. Pass 'uuid' to dissect a whole class, or 'handles' to dissect only specific hex nodes, which must be direct children of a class because the dissector works on class relative offsets. Requires an attached process. This is destructive to the current layout but undoable with undo_last_change; call suggest_types first if you want to see the guesses before committing.",
                Schema.Object(
                    Schema.Optional("uuid", Schema.Uuid(), "Uuid of the class whose hex nodes are dissected"),
                    Schema.Optional("handles", Schema.ArrayOf(Schema.Text(), MaxBatch), "Specific hex node handles to dissect, at most 256; a single string is also accepted"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip in the returned node list, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, MaxListLimit), "Items to return, default 200")),
                Schema.Object(
                    Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "The children of every affected class after the dissection as {handle, type, name, comment, offset, size}"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Total child count across the affected classes"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow"),
                    Schema.Required("replaced", Schema.Integer(), "Number of hex nodes that were actually retyped")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => DissectNodes(arguments, token)));
        }

        private ToolResult ListNodeTypes(ToolArguments arguments)
        {
            var category = arguments.OptionalString("category", null);
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", DefaultListLimit, MaxListLimit);

            var matches = NodeTypeCatalog.All
                .Where(entry => category == null || string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Category, StringComparer.Ordinal)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();

            var items = new JArray();

            foreach (var entry in matches.Skip(offset).Take(limit))
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

            return ToolResult.Ok(Format.Page(items, offset, limit, matches.Count));
        }

        private ToolResult AddNodes(ToolArguments arguments, CancellationToken token)
        {
            var requests = ParseRequests(arguments, "nodes", "parent");

            var structured = context.Project.Mutate("add_node", project =>
            {
                var containers = new List<BaseContainerNode>();
                var targets = new List<BaseContainerNode>(requests.Count);

                foreach (var request in requests)
                {
                    var container = context.Project.ResolveContainer(project, request.Target);

                    targets.Add(container);
                    Track(containers, container);
                }

                var created = new List<BaseNode>(requests.Count);

                Begin(containers);

                try
                {
                    for (var i = 0; i < requests.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();

                        var node = CreateNode(requests[i]);

                        try
                        {
                            targets[i].AddNode(node);
                        }
                        catch (ArgumentException)
                        {
                            throw Rejected(targets[i], node);
                        }

                        created.Add(node);
                    }
                }
                finally
                {
                    End(containers);
                }

                return Listing(created);
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult InsertNodes(ToolArguments arguments, CancellationToken token)
        {
            var requests = ParseRequests(arguments, "nodes", "before");

            var structured = context.Project.Mutate("insert_node", project =>
            {
                var containers = new List<BaseContainerNode>();
                var targets = new List<BaseContainerNode>(requests.Count);
                var positions = new List<BaseNode>(requests.Count);

                foreach (var request in requests)
                {
                    var position = context.Project.Resolve(project, request.Target);
                    var container = ParentOf(position, request.Target);

                    positions.Add(position);
                    targets.Add(container);
                    Track(containers, container);
                }

                var created = new List<BaseNode>(requests.Count);

                Begin(containers);

                try
                {
                    for (var i = 0; i < requests.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();

                        var node = CreateNode(requests[i]);

                        try
                        {
                            targets[i].InsertNode(positions[i], node);
                        }
                        catch (ArgumentException)
                        {
                            throw Rejected(targets[i], node);
                        }

                        created.Add(node);
                    }
                }
                finally
                {
                    End(containers);
                }

                return Listing(created);
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult DeleteNodes(ToolArguments arguments, CancellationToken token)
        {
            var handles = ParseHandles(arguments, "handles");

            var structured = context.Project.Mutate("delete_node", project =>
            {
                var containers = new List<BaseContainerNode>();
                var grouped = new List<List<BaseNode>>();

                foreach (var handle in handles)
                {
                    var node = context.Project.Resolve(project, handle);
                    var container = ParentOf(node, handle);

                    var index = containers.IndexOf(container);
                    if (index < 0)
                    {
                        containers.Add(container);
                        grouped.Add(new List<BaseNode>());

                        index = containers.Count - 1;
                    }

                    if (!grouped[index].Contains(node))
                    {
                        grouped[index].Add(node);
                    }
                }

                var deleted = 0;

                Begin(containers);

                try
                {
                    for (var i = 0; i < containers.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();

                        var container = containers[i];
                        var nodes = grouped[i];

                        nodes.Sort((left, right) => container.FindNodeIndex(right).CompareTo(container.FindNodeIndex(left)));

                        foreach (var node in nodes)
                        {
                            if (container.RemoveNode(node))
                            {
                                ++deleted;
                            }
                        }
                    }
                }
                finally
                {
                    End(containers);
                }

                var affected = new JArray();

                foreach (var container in containers)
                {
                    affected.Add(Describe(context.Project.OwnerOf(container), container));
                }

                return new JObject
                {
                    ["deleted"] = deleted,
                    ["handles"] = affected
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult ChangeNodeTypes(ToolArguments arguments, CancellationToken token)
        {
            var entries = Entries(arguments, "nodes");
            var requests = new List<NodeRequest>(entries.Count);

            foreach (var entry in entries)
            {
                var item = new ToolArguments(entry);

                requests.Add(new NodeRequest
                {
                    Target = NodeHandle.Parse(item.String("handle")),
                    Type = NodeTypeCatalog.Require(item.String("type"))
                });
            }

            var structured = context.Project.Mutate("change_node_type", project =>
            {
                var containers = new List<BaseContainerNode>();
                var targets = new List<BaseContainerNode>(requests.Count);
                var originals = new List<BaseNode>(requests.Count);

                foreach (var request in requests)
                {
                    var node = context.Project.Resolve(project, request.Target);
                    var container = ParentOf(node, request.Target);

                    originals.Add(node);
                    targets.Add(container);
                    Track(containers, container);
                }

                var items = new JArray();

                Begin(containers);

                try
                {
                    for (var i = 0; i < requests.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();

                        var container = targets[i];
                        var original = originals[i];

                        //
                        // ReplaceChildNode compensates shrinking only: freed bytes come back as
                        // hex nodes, while a larger replacement quietly eats the bytes of
                        // whatever follows it. The host says nothing about that, so the size is
                        // taken before and after and the difference is handed back as a warning.
                        // CopyFromNode is just as lossy for a text node, which keeps the byte
                        // length and drops name, comment and offset.
                        //
                        var previousType = original.GetType().Name;
                        var previousSize = SizeOf(original);
                        var replacement = CreateNode(requests[i]);

                        try
                        {
                            container.ReplaceChildNode(original, replacement);
                        }
                        catch (ArgumentException)
                        {
                            throw Rejected(container, replacement);
                        }

                        var size = SizeOf(replacement);
                        var item = Describe(context.Project.OwnerOf(replacement), replacement);

                        item["previousType"] = previousType;
                        item["previousSize"] = previousSize;

                        if (size > previousSize)
                        {
                            var grown = size - previousSize;

                            item["warning"] = $"'{replacement.GetType().Name}' is {grown} bytes larger than the '{previousType}' it replaced. ReClass.NET compensates shrinking only, so those {grown} bytes were absorbed from whatever follows inside '{container.GetType().Name}'. Re-read the class and repair the following fields.";
                        }

                        items.Add(item);
                    }
                }
                finally
                {
                    End(containers);
                }

                return new JObject
                {
                    ["nodes"] = items,
                    ["count"] = items.Count
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult ApplyText(ToolArguments arguments, CancellationToken token, bool isName)
        {
            var field = isName ? "name" : "comment";
            var entries = Entries(arguments, "nodes");

            var handles = new List<NodeHandle>(entries.Count);
            var values = new List<string>(entries.Count);

            foreach (var entry in entries)
            {
                var item = new ToolArguments(entry);

                handles.Add(NodeHandle.Parse(item.String("handle")));
                values.Add(item.String(field));
            }

            var structured = context.Project.Mutate(isName ? "set_node_name" : "set_node_comment", project =>
            {
                var nodes = new List<BaseNode>(handles.Count);

                foreach (var handle in handles)
                {
                    nodes.Add(context.Project.Resolve(project, handle));
                }

                var items = new JArray();

                for (var i = 0; i < nodes.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    if (isName)
                    {
                        nodes[i].Name = values[i];
                    }
                    else
                    {
                        nodes[i].Comment = values[i];
                    }

                    items.Add(Describe(context.Project.OwnerOf(nodes[i]), nodes[i]));
                }

                return new JObject
                {
                    ["nodes"] = items,
                    ["count"] = items.Count
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SetNodeSize(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));

            var hasLength = arguments.Has("length");
            var hasCount = arguments.Has("count");
            var hasBits = arguments.Has("bits");

            if ((hasLength ? 1 : 0) + (hasCount ? 1 : 0) + (hasBits ? 1 : 0) != 1)
            {
                throw new InvalidArgumentsException("Pass exactly one of 'length' for a text node, 'count' for an array node or 'bits' for a BitFieldNode");
            }

            var length = hasLength ? Positive(arguments, "length", MaxTextLength) : 0;
            var count = hasCount ? Positive(arguments, "count", MaxArrayCount) : 0;
            var bits = hasBits ? Positive(arguments, "bits", MaxBits) : 0;

            var structured = context.Project.Mutate("set_node_size", project =>
            {
                var node = context.Project.Resolve(project, handle);
                var previousSize = SizeOf(node);
                var container = node.GetParentContainer();

                container?.BeginUpdate();

                try
                {
                    if (hasLength)
                    {
                        ApplyLength(node, length);
                    }
                    else if (hasCount)
                    {
                        ApplyCount(node, count);
                    }
                    else
                    {
                        ApplyBits(node, bits);
                    }
                }
                finally
                {
                    container?.EndUpdate();
                }

                return new JObject
                {
                    ["node"] = Describe(context.Project.OwnerOf(node), node),
                    ["previousSize"] = previousSize
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SetWrappedType(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));
            var type = NodeTypeCatalog.Require(arguments.String("type"));

            var structured = context.Project.Mutate("set_wrapped_type", project =>
            {
                var node = context.Project.Resolve(project, handle);

                if (!(node is BaseWrapperNode wrapper))
                {
                    throw new ToolException(
                        $"Node '{handle}' is a '{node.GetType().Name}', which does not wrap an inner node",
                        "Only pointer and array nodes wrap something; call change_node_type to retype a plain node.");
                }

                var inner = Instantiate(type);

                if (!wrapper.CanChangeInnerNodeTo(inner))
                {
                    throw new ToolException(
                        $"'{wrapper.GetType().Name}' cannot wrap a '{inner.GetType().Name}'",
                        "Pointers and arrays reject a bare class and a virtual method; wrap a ClassInstanceNode instead and point it at a class with set_class_reference.");
                }

                var container = wrapper.GetParentContainer();

                container?.BeginUpdate();

                try
                {
                    wrapper.ChangeInnerNode(inner);
                }
                finally
                {
                    container?.EndUpdate();
                }

                return new JObject
                {
                    ["wrapper"] = Describe(context.Project.OwnerOf(wrapper), wrapper),
                    ["inner"] = Describe(context.Project.OwnerOf(inner), inner)
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SetClassReference(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));
            var uuid = arguments.Uuid("uuid");

            var structured = context.Project.Mutate("set_class_reference", project =>
            {
                var node = context.Project.Resolve(project, handle);

                if (!(node is BaseClassWrapperNode wrapper))
                {
                    throw new ToolException(
                        $"Node '{handle}' is a '{node.GetType().Name}', which does not reference a class",
                        "Only a ClassInstanceNode references a class. Call set_wrapped_type with type ClassInstanceNode on the pointer or array first, then use the returned inner handle here.");
                }

                var target = context.Project.RequireClass(project, uuid);
                var parent = wrapper.GetParentClass();

                if (parent == null)
                {
                    throw new ToolException(
                        $"Node '{handle}' is not inside a class, so a class reference cannot be validated",
                        "Re-read the class with get_class and use a handle taken from it.");
                }

                //
                // The cycle question has to be asked before the wiring and not after. ReClass.NET
                // walks the class graph while it draws, and a class that becomes reachable from
                // its own parent makes that walk recurse until the process dies. A pointer in the
                // chain breaks the recursion because it is only followed on demand, which is what
                // GetRootWrapperNode is being asked about here.
                //
                var cyclic = ClassUtil.IsCyclicIfClassIsAccessibleFromParent(parent, target, project.Classes);
                var indirect = !wrapper.GetRootWrapperNode().ShouldPerformCycleCheckForInnerNode();

                if (cyclic && !indirect)
                {
                    throw new ToolException(
                        $"Referencing class '{target.Name}' from '{handle}' would create a class cycle, because '{target.Name}' becomes reachable from '{parent.Name}' again",
                        "ReClass.NET would recurse while drawing. Put a PointerNode in front of the class instance to model a self referencing structure such as a linked list or a tree.");
                }

                if (!wrapper.CanChangeInnerNodeTo(target))
                {
                    throw new ToolException(
                        $"'{wrapper.GetType().Name}' cannot reference a class",
                        "Use a ClassInstanceNode, which is the only node type that holds a class reference.");
                }

                var previous = wrapper.InnerNode as ClassNode;
                var container = wrapper.GetParentContainer();

                container?.BeginUpdate();

                try
                {
                    wrapper.ChangeInnerNode(target);
                }
                finally
                {
                    container?.EndUpdate();
                }

                var result = new JObject
                {
                    ["node"] = Describe(context.Project.OwnerOf(wrapper), wrapper),
                    ["referencedClass"] = new JObject
                    {
                        ["uuid"] = target.Uuid.ToString("D"),
                        ["name"] = target.Name,
                        ["size"] = SizeOf(target)
                    },
                    ["previousClassUuid"] = previous == null ? string.Empty : previous.Uuid.ToString("D")
                };

                if (cyclic)
                {
                    result["warning"] = $"'{target.Name}' is now reachable from '{parent.Name}' again. This is only safe because the reference sits behind a pointer, which ReClass.NET follows on demand.";
                }

                return result;
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult AddBytes(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));
            var size = Positive(arguments, "size", MaxBytes);

            var structured = context.Project.Mutate("add_bytes", project =>
            {
                var container = context.Project.ResolveContainer(project, handle);
                var before = container.Nodes.Count;

                container.BeginUpdate();

                try
                {
                    container.AddBytes(size);
                }
                finally
                {
                    container.EndUpdate();
                }

                var owner = context.Project.OwnerOf(container);
                var added = new JArray();

                for (var i = before; i < container.Nodes.Count; ++i)
                {
                    added.Add(Describe(owner, container.Nodes[i]));
                }

                return new JObject
                {
                    ["container"] = Describe(owner, container),
                    ["added"] = added,
                    ["count"] = added.Count
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult InsertBytes(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));
            var size = Positive(arguments, "size", MaxBytes);

            var structured = context.Project.Mutate("insert_bytes", project =>
            {
                var position = context.Project.Resolve(project, handle);
                var container = ParentOf(position, handle);
                var start = container.FindNodeIndex(position);

                container.BeginUpdate();

                try
                {
                    container.InsertBytes(position, size);
                }
                finally
                {
                    container.EndUpdate();
                }

                var owner = context.Project.OwnerOf(container);
                var inserted = new JArray();
                var end = container.FindNodeIndex(position);

                for (var i = start; i < end; ++i)
                {
                    inserted.Add(Describe(owner, container.Nodes[i]));
                }

                return new JObject
                {
                    ["container"] = Describe(owner, container),
                    ["inserted"] = inserted,
                    ["count"] = inserted.Count,
                    ["position"] = Describe(owner, position)
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SuggestTypes(ToolArguments arguments, CancellationToken token)
        {
            var uuid = arguments.Uuid("uuid");
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", DefaultListLimit, MaxListLimit);

            var process = context.RequireProcess();

            //
            // Only the hex nodes and their handles are collected under the project hop. GuessNode
            // reads memory and hands back a throwaway node without touching the tree, so the
            // guessing itself is fine to run out here, off the ui thread.
            //
            var snapshot = context.Project.Read(project =>
            {
                var owner = context.Project.RequireClass(project, uuid);

                var result = new ClassSnapshot
                {
                    Name = owner.Name,
                    Formula = owner.AddressFormula,
                    Size = SizeOf(owner)
                };

                foreach (var child in owner.Nodes)
                {
                    if (child is BaseHexNode hex)
                    {
                        result.Targets.Add(new HexTarget
                        {
                            Node = hex,
                            Handle = NodeHandle.Format(owner, hex),
                            Offset = hex.Offset,
                            Type = hex.GetType().Name
                        });
                    }
                }

                return result;
            });

            if (snapshot.Targets.Count == 0)
            {
                return ToolResult.Ok(Format.Page(new JArray(), offset, limit, 0));
            }

            var memory = FillBuffer(process, snapshot.Formula, snapshot.Size, snapshot.Name);

            var items = new JArray();
            var total = 0;

            foreach (var target in snapshot.Targets)
            {
                token.ThrowIfCancellationRequested();

                if (!NodeDissector.GuessNode(target.Node, process, memory, out var guessed) || guessed == null)
                {
                    continue;
                }

                if (total >= offset && items.Count < limit)
                {
                    items.Add(new JObject
                    {
                        ["handle"] = target.Handle,
                        ["offset"] = target.Offset,
                        ["currentType"] = target.Type,
                        ["suggestedType"] = guessed.GetType().Name
                    });
                }

                ++total;
            }

            return ToolResult.Ok(Format.Page(items, offset, limit, total));
        }

        private ToolResult DissectNodes(ToolArguments arguments, CancellationToken token)
        {
            var hasUuid = arguments.Has("uuid");
            var hasHandles = arguments.Has("handles");

            if (hasUuid == hasHandles)
            {
                throw new InvalidArgumentsException("Pass either 'uuid' to dissect every hex node of a class or 'handles' to dissect specific hex nodes");
            }

            var uuid = hasUuid ? arguments.Uuid("uuid") : Guid.Empty;
            var handles = hasHandles ? ParseHandles(arguments, "handles") : new List<NodeHandle>();
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", DefaultListLimit, MaxListLimit);

            var process = context.RequireProcess();

            var structured = context.Project.Mutate("dissect_nodes", project =>
            {
                var owners = new List<ClassNode>();
                var grouped = new List<List<BaseHexNode>>();

                if (hasUuid)
                {
                    var owner = context.Project.RequireClass(project, uuid);
                    var group = new List<BaseHexNode>();

                    foreach (var child in owner.Nodes)
                    {
                        if (child is BaseHexNode hex)
                        {
                            group.Add(hex);
                        }
                    }

                    owners.Add(owner);
                    grouped.Add(group);
                }
                else
                {
                    foreach (var handle in handles)
                    {
                        var node = context.Project.Resolve(project, handle);

                        if (!(node is BaseHexNode hex))
                        {
                            throw new ToolException(
                                $"Node '{handle}' is a '{node.GetType().Name}', not a hex node",
                                "dissect_nodes only retypes undecoded hex nodes; call suggest_types to see which ones are left.");
                        }

                        if (!(hex.ParentNode is ClassNode owner))
                        {
                            throw new ToolException(
                                $"Node '{handle}' is not a direct child of a class",
                                "The dissector works on class relative offsets, so it only accepts the direct hex children of a class.");
                        }

                        var index = owners.IndexOf(owner);
                        if (index < 0)
                        {
                            owners.Add(owner);
                            grouped.Add(new List<BaseHexNode>());

                            index = owners.Count - 1;
                        }

                        if (!grouped[index].Contains(hex))
                        {
                            grouped[index].Add(hex);
                        }
                    }
                }

                var replaced = 0;

                for (var i = 0; i < owners.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    var owner = owners[i];
                    var group = grouped[i];

                    if (group.Count == 0)
                    {
                        continue;
                    }

                    var memory = FillBuffer(process, owner.AddressFormula, SizeOf(owner), owner.Name);

                    //
                    // DissectNodes rewrites the live tree, so unlike GuessNode it has to run
                    // inside the mutation hop and inside BeginUpdate. The host reports nothing
                    // about what it did either: a hex node that is no longer among the children
                    // afterwards is how a replacement gets counted.
                    //
                    owner.BeginUpdate();

                    try
                    {
                        NodeDissector.DissectNodes(group, process, memory);
                    }
                    finally
                    {
                        owner.EndUpdate();
                    }

                    var survivors = new HashSet<BaseNode>(owner.Nodes);

                    foreach (var node in group)
                    {
                        if (!survivors.Contains(node))
                        {
                            ++replaced;
                        }
                    }
                }

                var items = new JArray();
                var total = 0;

                foreach (var owner in owners)
                {
                    foreach (var child in owner.Nodes)
                    {
                        if (total >= offset && items.Count < limit)
                        {
                            items.Add(Describe(owner, child));
                        }

                        ++total;
                    }
                }

                var page = Format.Page(items, offset, limit, total);
                page["replaced"] = replaced;

                return page;
            });

            return ToolResult.Ok(structured);
        }

        private JObject Listing(List<BaseNode> nodes)
        {
            var items = new JArray();

            foreach (var node in nodes)
            {
                items.Add(Describe(context.Project.OwnerOf(node), node));
            }

            return new JObject
            {
                ["nodes"] = items,
                ["count"] = items.Count
            };
        }

        private static MemoryBuffer FillBuffer(RemoteProcess process, string formula, int size, string className)
        {
            if (size <= 0)
            {
                throw new ToolException(
                    $"Class '{className}' has no bytes to read",
                    "Call add_bytes on the class first so the dissector has something to look at.");
            }

            IntPtr address;

            try
            {
                address = AddressResolver.Resolve(process, formula ?? string.Empty);
            }
            catch (ParseException ex)
            {
                throw new ToolException(
                    $"The address formula '{formula}' of class '{className}' is not valid: {ex.Message}",
                    "Module names must be wrapped in angle brackets and every number is hexadecimal, for example <game.exe>+1f4. Fix it with set_class_address.");
            }

            //
            // MemoryBuffer.UpdateFrom fills itself with zeroes when the read fails and records
            // the failure in ContainsValidData alone. Ignoring that flag would hand the dissector
            // a page of zeroes, and it would happily type the whole class as padding.
            //
            var memory = new MemoryBuffer { Size = size };
            memory.UpdateFrom(process, address);

            if (!memory.ContainsValidData)
            {
                throw new ToolException(
                    $"Could not read {size} bytes at {Format.Hex(address)} for class '{className}'",
                    "That address is not readable in the attached process; check it with resolve_address and correct it with set_class_address.");
            }

            return memory;
        }

        private static JObject EntrySchema(string targetName, string targetDescription)
        {
            return Schema.Object(
                Schema.Required(targetName, Schema.Text(), targetDescription),
                Schema.Required("type", Schema.Text(), "Node type name from list_node_types"),
                Schema.Optional("name", Schema.Text(), "Field name"),
                Schema.Optional("comment", Schema.Text(), "Field comment"),
                Schema.Optional("count", Schema.Integer(1, MaxArrayCount), "Element count, array node types only"),
                Schema.Optional("length", Schema.Integer(1, MaxTextLength), "Character count, text node types only"),
                Schema.Optional("bits", Schema.Integer(1, MaxBits), "Bit width, BitFieldNode only, snapped to 8, 16, 32 or 64"));
        }

        private static List<NodeRequest> ParseRequests(ToolArguments arguments, string name, string targetName)
        {
            var entries = Entries(arguments, name);
            var requests = new List<NodeRequest>(entries.Count);

            foreach (var entry in entries)
            {
                var item = new ToolArguments(entry);

                var request = new NodeRequest
                {
                    Target = NodeHandle.Parse(item.String(targetName)),
                    Type = NodeTypeCatalog.Require(item.String("type")),
                    Name = item.OptionalString("name", null),
                    Comment = item.OptionalString("comment", null)
                };

                if (item.Has("count"))
                {
                    request.HasCount = true;
                    request.Count = Positive(item, "count", MaxArrayCount);
                }

                if (item.Has("length"))
                {
                    request.HasLength = true;
                    request.Length = Positive(item, "length", MaxTextLength);
                }

                if (item.Has("bits"))
                {
                    request.HasBits = true;
                    request.Bits = Positive(item, "bits", MaxBits);
                }

                requests.Add(request);
            }

            return requests;
        }

        private static List<NodeHandle> ParseHandles(ToolArguments arguments, string name)
        {
            var texts = arguments.Strings(name);

            if (texts.Count == 0)
            {
                throw new InvalidArgumentsException($"'{name}' must contain at least one node handle");
            }

            if (texts.Count > MaxBatch)
            {
                throw new InvalidArgumentsException($"'{name}' must not contain more than {MaxBatch} entries");
            }

            var handles = new List<NodeHandle>(texts.Count);

            foreach (var text in texts)
            {
                handles.Add(NodeHandle.Parse(text));
            }

            return handles;
        }

        private static IReadOnlyList<JObject> Entries(ToolArguments arguments, string name)
        {
            var entries = arguments.Objects(name);

            if (entries.Count == 0)
            {
                throw new InvalidArgumentsException($"'{name}' must contain at least one entry");
            }

            if (entries.Count > MaxBatch)
            {
                throw new InvalidArgumentsException($"'{name}' must not contain more than {MaxBatch} entries");
            }

            return entries;
        }

        private static int Positive(ToolArguments arguments, string name, int maximum)
        {
            if (!arguments.Has(name))
            {
                throw new InvalidArgumentsException($"Missing required argument '{name}'");
            }

            var value = arguments.Count(name, 0, maximum);

            if (value < 1)
            {
                throw new InvalidArgumentsException($"'{name}' must be at least 1");
            }

            return value;
        }

        private static BaseNode Instantiate(NodeTypeEntry type)
        {
            var node = BaseNode.CreateInstanceFromType(type.Type, true);

            if (node == null)
            {
                throw new ToolException(
                    $"'{type.Name}' could not be instantiated",
                    "Call list_node_types for the creatable type names.");
            }

            return node;
        }

        private static BaseNode CreateNode(NodeRequest request)
        {
            var node = Instantiate(request.Type);

            if (request.Name != null)
            {
                node.Name = request.Name;
            }

            if (request.Comment != null)
            {
                node.Comment = request.Comment;
            }

            if (request.HasLength)
            {
                ApplyLength(node, request.Length);
            }

            if (request.HasCount)
            {
                ApplyCount(node, request.Count);
            }

            if (request.HasBits)
            {
                ApplyBits(node, request.Bits);
            }

            return node;
        }

        private static void ApplyLength(BaseNode node, int length)
        {
            if (!(node is BaseTextNode text))
            {
                throw new ToolException($"'{node.GetType().Name}' has no character length", SizeHint(node));
            }

            text.Length = length;
        }

        private static void ApplyCount(BaseNode node, int count)
        {
            if (!(node is BaseWrapperArrayNode array))
            {
                throw new ToolException($"'{node.GetType().Name}' has no element count", SizeHint(node));
            }

            array.Count = count;

            if (array.CurrentIndex >= count)
            {
                array.CurrentIndex = count - 1;
            }
        }

        private static void ApplyBits(BaseNode node, int bits)
        {
            if (!(node is BitFieldNode field))
            {
                throw new ToolException($"'{node.GetType().Name}' has no bit width", SizeHint(node));
            }

            field.Bits = bits;
        }

        private static string SizeHint(BaseNode node)
        {
            if (node is BaseTextNode)
            {
                return "A text node takes 'length', the character count.";
            }

            if (node is BaseWrapperArrayNode)
            {
                return "An array node takes 'count', the element count.";
            }

            if (node is BitFieldNode)
            {
                return "A BitFieldNode takes 'bits', snapped to 8, 16, 32 or 64.";
            }

            return "Only text nodes ('length'), array nodes ('count') and BitFieldNode ('bits') carry their own size; every other type has a fixed size, change the node type instead.";
        }

        //
        // A container refuses a child with a bare ArgumentException that carries no message at
        // all, so every caller catches it and comes here to name the container, the child it
        // rejected and what to do instead.
        //
        private static ToolException Rejected(BaseContainerNode container, BaseNode node)
        {
            if (container is VirtualMethodTableNode)
            {
                return new ToolException(
                    $"A VirtualMethodTableNode cannot hold a '{node.GetType().Name}' child",
                    "A vtable accepts virtual method slots only, and those are not creatable by name; call add_bytes on the vtable handle to append one slot per pointer size.");
            }

            return new ToolException(
                $"'{container.GetType().Name}' cannot hold a '{node.GetType().Name}' child",
                "Classes and unions reject a bare class and a virtual method; add a ClassInstanceNode and point it at the class with set_class_reference.");
        }

        private static BaseContainerNode ParentOf(BaseNode node, NodeHandle handle)
        {
            if (node.ParentNode is BaseContainerNode container)
            {
                return container;
            }

            if (node.ParentNode is BaseWrapperNode)
            {
                throw new ToolException(
                    $"Node '{handle}' is the inner node of a wrapper and is not a container child",
                    "Call set_wrapped_type on the pointer or array handle to change what it wraps.");
            }

            throw new ToolException(
                $"Node '{handle}' is a class root, which has no parent container",
                "Call delete_class to remove a class, or pass the handle of one of its children.");
        }

        private static void Track(List<BaseContainerNode> containers, BaseContainerNode container)
        {
            if (!containers.Contains(container))
            {
                containers.Add(container);
            }
        }

        //
        // Every affected container is opened once and closed in reverse, so a batch that spans
        // nested containers costs one host update instead of one per node. The deduplication in
        // Track belongs to this: BeginUpdate nests by count and a container skips UpdateOffsets
        // while that count is above zero, so an unbalanced pair leaves stale offsets behind.
        //
        private static void Begin(List<BaseContainerNode> containers)
        {
            foreach (var container in containers)
            {
                container.BeginUpdate();
            }
        }

        private static void End(List<BaseContainerNode> containers)
        {
            for (var i = containers.Count - 1; i >= 0; --i)
            {
                containers[i].EndUpdate();
            }
        }

        private static JObject Describe(ClassNode owner, BaseNode node)
        {
            return new JObject
            {
                ["handle"] = NodeHandle.Format(owner, node),
                ["type"] = node.GetType().Name,
                ["name"] = node.Name ?? string.Empty,
                ["comment"] = node.Comment ?? string.Empty,
                ["offset"] = node.Offset,
                ["size"] = SizeOf(node)
            };
        }

        //
        // MemorySize is computed and some nodes throw instead of returning zero: a union without
        // children throws out of Max(), an array without an inner node dereferences null. Describe
        // runs on nodes that were created moments ago, and a zero is a better answer than losing
        // the whole batch to a node the host cannot measure yet.
        //
        private static int SizeOf(BaseNode node)
        {
            try
            {
                return node.MemorySize;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private sealed class NodeRequest
        {
            public NodeHandle Target { get; set; }

            public NodeTypeEntry Type { get; set; }

            public string Name { get; set; }

            public string Comment { get; set; }

            public bool HasCount { get; set; }

            public int Count { get; set; }

            public bool HasLength { get; set; }

            public int Length { get; set; }

            public bool HasBits { get; set; }

            public int Bits { get; set; }
        }

        private sealed class HexTarget
        {
            public BaseHexNode Node { get; set; }

            public string Handle { get; set; }

            public int Offset { get; set; }

            public string Type { get; set; }
        }

        private sealed class ClassSnapshot
        {
            public string Name { get; set; }

            public string Formula { get; set; }

            public int Size { get; set; }

            public List<HexTarget> Targets { get; } = new List<HexTarget>();
        }
    }
}
