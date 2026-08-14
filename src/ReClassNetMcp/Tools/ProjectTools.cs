using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.AddressParser;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class ProjectTools
    {
        private static readonly string[] ClassFields = { "uuid", "name", "address", "nodeCount", "memorySize", "comment" };

        private readonly ToolContext context;

        public ProjectTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "project_info",
                "Project info",
                "Report the open project: its file path, the class and enum counts, the class currently selected in the ReClass.NET window, and the host pointer size and platform. Call list_classes to enumerate the classes themselves.",
                Schema.Object(),
                Schema.Object(Schema.Optional("path", Schema.Text(), "Absolute path the project was last opened from or saved to, null when never saved"),
                    Schema.Required("classCount", Schema.Integer(), "Number of classes in the project"),
                    Schema.Required("enumCount", Schema.Integer(), "Number of enums in the project"),
                    Schema.Required("selectedClass", Schema.AnyObject(), "The selected class as {uuid, name}, both null when nothing is selected"),
                    Schema.Required("pointerSize", Schema.Integer(), "Pointer size of the ReClass.NET host in bytes"),
                    Schema.Required("platform", Schema.Text(), "Bitness of the ReClass.NET host")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ProjectInfo()));

            registry.Add(new ToolDefinition(
                "list_classes",
                "List classes",
                "List the classes of the open project with their address formula, node count and memory size. Filter by a case insensitive substring of the class name, page with offset and limit, and pass fields to return only the members you need. The returned uuid is the handle every other class and node tool takes; call get_class with it to see the layout.",
                Schema.Object(
                    Schema.Optional("filter", Schema.Text(), "Case insensitive substring matched against the class name"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Items to return, default 100"),
                    Schema.Optional("fields", Schema.ArrayOf(Schema.Enum("uuid", "name", "address", "nodeCount", "memorySize", "comment")), "Members to include in each item, all of them when omitted")),
                Schema.Object(Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Classes as {uuid, name, address, nodeCount, memorySize, comment}, projected by fields"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Matching class count"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListClasses(arguments, token)));

            registry.Add(new ToolDefinition(
                "get_class",
                "Get class",
                "Read the layout of one class: its address formula, memory size, comment and its child nodes with handle, hex offset from the class start, type, name, comment and size in bytes. depth 1 returns only the direct children, higher values inline the children of containers (union, virtual method table) and of wrappers (array, pointer). A node that points at another class reports 'reference' with that class uuid instead of inlining it, so call get_class again with that uuid. Index paths inside a handle shift when nodes are inserted or removed, so always use the handles from the most recent call.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes or create_class"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Child nodes to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Child nodes to return, default 200"),
                    Schema.Optional("depth", Schema.Integer(1, 4), "How many node levels to inline, default 1")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Class uuid"),
                    Schema.Required("name", Schema.Text(), "Class name"),
                    Schema.Required("addressFormula", Schema.Text(), "Address formula the class is read from"),
                    Schema.Required("memorySize", Schema.Integer(), "Total size of the class in bytes"),
                    Schema.Required("comment", Schema.Text(), "Class comment"),
                    Schema.Required("nodes", Schema.Object(Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Nodes as {handle, index, offset, type, name, comment, memorySize, hidden} plus type specific members"),
                        Schema.Required("offset", Schema.Integer(), "Applied offset"),
                        Schema.Required("limit", Schema.Integer(), "Applied limit"),
                        Schema.Required("count", Schema.Integer(), "Returned item count"),
                        Schema.Required("total", Schema.Integer(), "Total child node count"),
                        Schema.Required("hasMore", Schema.Bool(), "True when more items follow")), "The paged child nodes of the class")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => GetClass(arguments, token)));

            registry.Add(new ToolDefinition(
                "create_class",
                "Create class",
                "Create a new class in the open project and fill it with placeholder hex nodes. address is a ReClass.NET address formula, not a resolved address: module names are wrapped in angle brackets, every number is hexadecimal (10 means 0x10), [x] dereferences, and + - * / are supported, for example [<game.exe>+0x1f4]+0x10. addBytes defaults to one pointer size; pass 0 to create the class with no nodes at all. Returns the uuid to pass to get_class, the node tools and generate_code.",
                Schema.Object(
                    Schema.Optional("name", Schema.Text(1, 256), "Class name, a generated placeholder when omitted"),
                    Schema.Optional("address", Schema.Formula(), "Address formula the class is read from"),
                    Schema.Optional("addBytes", Schema.Integer(0, 65536), "Placeholder bytes to add, default is one pointer size")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Uuid of the new class"),
                    Schema.Required("name", Schema.Text(), "Name of the new class"),
                    Schema.Required("addressFormula", Schema.Text(), "Address formula of the new class"),
                    Schema.Required("handle", Schema.Text(), "Node handle of the class root")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => CreateClass(arguments)));

            registry.Add(new ToolDefinition(
                "rename_class",
                "Rename class",
                "Rename a class. generate_code emits the name verbatim as the struct name, so pass a valid identifier. Returns the previous name.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes"),
                    Schema.Required("name", Schema.Text(1, 256), "The new class name")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Class uuid"),
                    Schema.Required("name", Schema.Text(), "The name now in effect"),
                    Schema.Required("previousName", Schema.Text(), "The name before the call")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => RenameClass(arguments)));

            registry.Add(new ToolDefinition(
                "set_class_address",
                "Set class address",
                "Set the address formula of a class, which is where ReClass.NET reads its memory from. Syntax: module names are wrapped in angle brackets, every number is hexadecimal (10 means 0x10), [x] dereferences, and + - * / are supported, for example [<game.exe>+0x1f4]+0x10. Only the syntax is checked here because ReClass.NET resolves the formula lazily while drawing; call resolve_address to confirm it lands where you expect.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes"),
                    Schema.Required("addressFormula", Schema.Formula(), "The new address formula")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Class uuid"),
                    Schema.Required("addressFormula", Schema.Text(), "The formula now in effect"),
                    Schema.Required("previousAddressFormula", Schema.Text(), "The formula before the call")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetClassAddress(arguments)));

            registry.Add(new ToolDefinition(
                "set_class_comment",
                "Set class comment",
                "Set the comment shown next to a class in ReClass.NET and emitted by generate_code. Pass an empty string to clear it.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes"),
                    Schema.Required("comment", Schema.Text(), "The new comment, empty to clear")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Class uuid"),
                    Schema.Required("comment", Schema.Text(), "The comment now in effect"),
                    Schema.Required("previousComment", Schema.Text(), "The comment before the call")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetClassComment(arguments)));

            registry.Add(new ToolDefinition(
                "delete_class",
                "Delete class",
                "Delete a class from the open project. This fails while another class still points at it through a class instance or class pointer node; the error names the referencing classes and their uuids so you can retype or delete those nodes first, or call remove_unused_classes.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Uuid of the deleted class"),
                    Schema.Required("name", Schema.Text(), "Name of the deleted class"),
                    Schema.Required("deleted", Schema.Bool(), "Always true on success"),
                    Schema.Required("remaining", Schema.Integer(), "Classes left in the project")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => DeleteClass(arguments)));

            registry.Add(new ToolDefinition(
                "remove_unused_classes",
                "Remove unused classes",
                "Delete every class that consists only of hex placeholder nodes and is not referenced by another class. This is the cleanup pass after dissecting a structure, because suggest_types and dissect_nodes leave empty helper classes behind. Reports how many classes were removed and how many remain.",
                Schema.Object(),
                Schema.Object(Schema.Required("removed", Schema.Integer(), "Number of classes removed"),
                    Schema.Required("remaining", Schema.Integer(), "Classes left in the project")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => RemoveUnusedClasses()));

            registry.Add(new ToolDefinition(
                "new_project",
                "New project",
                "Discard the open project and start an empty one. Everything unsaved is lost, so call save_project first if the layout matters. A snapshot is taken beforehand, so undo_last_change brings the old project back.",
                Schema.Object(),
                Schema.Object(Schema.Required("created", Schema.Bool(), "Always true on success")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => NewProject()));

            registry.Add(new ToolDefinition(
                "open_project",
                "Open project",
                "Replace the open project with one loaded from disk. path must be absolute and must exist. .rcnet is the ReClass.NET format and round-trips fully, while .reclass and .reclassqt are import only formats. A snapshot is taken beforehand, so undo_last_change brings the old project back.",
                Schema.Object(
                    Schema.Required("path", Schema.Text(1, 32767), "Absolute path of a .rcnet, .reclass or .reclassqt file")),
                Schema.Object(Schema.Required("path", Schema.Text(), "The loaded path"),
                    Schema.Required("classCount", Schema.Integer(), "Classes in the loaded project"),
                    Schema.Required("enumCount", Schema.Integer(), "Enums in the loaded project")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => OpenProject(arguments)));

            registry.Add(new ToolDefinition(
                "save_project",
                "Save project",
                "Write the open project to a .rcnet file. Without path the file the project was last opened from or saved to is reused, and the call fails when there is none. Reports the written path and the resulting file size in bytes.",
                Schema.Object(
                    Schema.Optional("path", Schema.Text(1, 32767), "Absolute path of the .rcnet file to write, reuses the project path when omitted")),
                Schema.Object(Schema.Required("path", Schema.Text(), "The written path"),
                    Schema.Required("classCount", Schema.Integer(), "Classes written"),
                    Schema.Required("bytes", Schema.Integer(), "Size of the written file in bytes")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SaveProject(arguments)));

            registry.Add(new ToolDefinition(
                "undo_last_change",
                "Undo last change",
                "Restore the whole project from the snapshot taken before the last mutating tool call. ReClass.NET itself has no undo, so this is the only way back. It is all or nothing: every change made after that snapshot is discarded, including changes the human made in the window. Call list_changes first to see which snapshot is newest.",
                Schema.Object(),
                Schema.Object(Schema.Required("restored", Schema.Bool(), "Always true on success")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => UndoLastChange()));

            registry.Add(new ToolDefinition(
                "list_changes",
                "List changes",
                "List the project snapshots taken before each mutating tool call, newest first, with the tool that caused it, the timestamp, the class count and the serialized size. undo_last_change restores the newest one.",
                Schema.Object(),
                Schema.Object(Schema.Required("snapshots", Schema.ArrayOf(Schema.AnyObject()), "Snapshots as {sequence, reason, takenAt, classCount, bytes}, newest first")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListChanges()));

            registry.Add(new ToolDefinition(
                "select_class",
                "Select class",
                "Select a class in the ReClass.NET window so the human watching sees the structure you are working on. This changes the view only, never the project.",
                Schema.Object(
                    Schema.Required("uuid", Schema.Uuid(), "Class uuid from list_classes")),
                Schema.Object(Schema.Required("uuid", Schema.Uuid(), "Uuid of the selected class"),
                    Schema.Required("name", Schema.Text(), "Name of the selected class")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => SelectClass(arguments)));

            registry.Add(new ToolDefinition(
                "get_selection",
                "Get selection",
                "Report the class currently selected in the ReClass.NET window, or nulls when nothing is selected. Use it to pick up the structure the human was last looking at.",
                Schema.Object(),
                Schema.Object(Schema.Optional("uuid", Schema.Uuid(), "Uuid of the selected class, null when nothing is selected"),
                    Schema.Optional("name", Schema.Text(), "Name of the selected class, null when nothing is selected")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => GetSelection()));
        }

        private ToolResult ProjectInfo()
        {
            var structured = context.Project.Read(project =>
            {
                var selected = context.Host.SelectedClass;

                return new JObject
                {
                    ["path"] = string.IsNullOrEmpty(project.Path) ? null : project.Path,
                    ["classCount"] = project.Classes.Count,
                    ["enumCount"] = project.Enums.Count,
                    ["selectedClass"] = new JObject
                    {
                        ["uuid"] = selected == null ? null : selected.Uuid.ToString("D"),
                        ["name"] = selected?.Name
                    }
                };
            });

            structured["pointerSize"] = context.Host.PointerSize;
            structured["platform"] = context.Host.Platform;

            return ToolResult.Ok(structured);
        }

        private ToolResult ListClasses(ToolArguments arguments, CancellationToken token)
        {
            var filter = arguments.OptionalString("filter", null);
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);
            var fields = SelectClassFields(arguments.Strings("fields"));

            var structured = context.Project.Read(project =>
            {
                var matches = new List<ClassNode>(project.Classes.Count);

                foreach (var node in project.Classes)
                {
                    if (filter != null && node.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches.Add(node);
                }

                var items = new JArray();

                for (var i = offset; i < matches.Count && items.Count < limit; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    items.Add(DescribeClass(matches[i], fields));
                }

                return Format.Page(items, offset, limit, matches.Count);
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult GetClass(ToolArguments arguments, CancellationToken token)
        {
            var uuid = arguments.Uuid("uuid");
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 200, 1000);
            var depth = Math.Max(1, arguments.Count("depth", 1, 4));

            var structured = context.Project.Read(project =>
            {
                var owner = context.Project.RequireClass(project, uuid);

                var items = new JArray();

                for (var i = offset; i < owner.Nodes.Count && items.Count < limit; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    items.Add(DescribeNode(owner, owner.Nodes[i], i, depth));
                }

                return new JObject
                {
                    ["uuid"] = owner.Uuid.ToString("D"),
                    ["name"] = owner.Name,
                    ["addressFormula"] = owner.AddressFormula,
                    ["memorySize"] = owner.MemorySize,
                    ["comment"] = owner.Comment,
                    ["nodes"] = Format.Page(items, offset, limit, owner.Nodes.Count)
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult CreateClass(ToolArguments arguments)
        {
            var name = arguments.OptionalString("name", null);
            var addressFormula = arguments.OptionalString("address", null);
            var addBytes = arguments.Count("addBytes", context.Host.PointerSize, 65536);

            if (name != null)
            {
                name = name.Trim();

                if (name.Length == 0)
                {
                    throw new InvalidArgumentsException("'name' must not be empty");
                }
            }

            if (addressFormula != null)
            {
                addressFormula = addressFormula.Trim();

                RequireFormula("address", addressFormula);
            }

            var structured = context.Project.Mutate("create_class", project =>
            {
                var node = ClassNode.Create();

                if (name != null)
                {
                    node.Name = name;
                }

                if (addressFormula != null)
                {
                    node.AddressFormula = addressFormula;
                }

                if (addBytes > 0)
                {
                    node.BeginUpdate();
                    node.AddBytes(addBytes);
                    node.EndUpdate();
                }

                //
                // ClassNode.Create() raises the static ClassCreated event, which the host has
                // wired to the current project, so the node is normally in it already by the
                // time we get here. AddClass without this check gives the project the same
                // class twice.
                //
                if (!project.ContainsClass(node.Uuid))
                {
                    project.AddClass(node);
                }

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["name"] = node.Name,
                    ["addressFormula"] = node.AddressFormula,
                    ["handle"] = NodeHandle.Format(node, node)
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult RenameClass(ToolArguments arguments)
        {
            var uuid = arguments.Uuid("uuid");
            var name = arguments.String("name").Trim();

            if (name.Length == 0)
            {
                throw new InvalidArgumentsException("'name' must not be empty");
            }

            var structured = context.Project.Mutate("rename_class", project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                var previous = node.Name;

                node.Name = name;

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["name"] = node.Name,
                    ["previousName"] = previous
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SetClassAddress(ToolArguments arguments)
        {
            var uuid = arguments.Uuid("uuid");
            var addressFormula = arguments.String("addressFormula").Trim();

            RequireFormula("addressFormula", addressFormula);

            var structured = context.Project.Mutate("set_class_address", project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                var previous = node.AddressFormula;

                node.AddressFormula = addressFormula;

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["addressFormula"] = node.AddressFormula,
                    ["previousAddressFormula"] = previous
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult SetClassComment(ToolArguments arguments)
        {
            var uuid = arguments.Uuid("uuid");
            var comment = arguments.String("comment");

            var structured = context.Project.Mutate("set_class_comment", project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                var previous = node.Comment;

                node.Comment = comment;

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["comment"] = node.Comment,
                    ["previousComment"] = previous
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult DeleteClass(ToolArguments arguments)
        {
            var uuid = arguments.Uuid("uuid");

            var structured = context.Project.Mutate("delete_class", project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                var name = node.Name;

                try
                {
                    project.Remove(node);
                }
                catch (ClassReferencedException ex)
                {
                    throw new ToolException(
                        $"The class '{name}' is still referenced by {DescribeReferences(ex)}",
                        "Retype or delete the referencing nodes with change_node_type or delete_node first, or call remove_unused_classes.");
                }

                return new JObject
                {
                    ["uuid"] = uuid.ToString("D"),
                    ["name"] = name,
                    ["deleted"] = true,
                    ["remaining"] = project.Classes.Count
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult RemoveUnusedClasses()
        {
            var structured = context.Project.Mutate("remove_unused_classes", project =>
            {
                var before = project.Classes.Count;

                project.RemoveUnusedClasses();

                var after = project.Classes.Count;

                return new JObject
                {
                    ["removed"] = before - after,
                    ["remaining"] = after
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult NewProject()
        {
            //
            // The snapshot has to be pushed while the old project is still the current one, so
            // the mutation body does nothing but read a count. ReplaceProject then swaps the
            // instance, and undo_last_change swaps the serialised copy back in.
            //
            context.Project.Mutate("new_project", project => project.Classes.Count);

            context.Host.ReplaceProject(new ReClassNetProject());

            return ToolResult.Ok(new JObject { ["created"] = true });
        }

        private ToolResult OpenProject(ToolArguments arguments)
        {
            var path = arguments.String("path").Trim();

            if (!Path.IsPathRooted(path))
            {
                throw new InvalidArgumentsException($"'path' must be absolute, got '{path}'");
            }

            if (!File.Exists(path))
            {
                throw new ToolException(
                    $"No file exists at '{path}'",
                    "Pass the absolute path of an existing .rcnet, .reclass or .reclassqt file.");
            }

            var extension = Path.GetExtension(path);
            extension = extension == null ? string.Empty : extension.ToLowerInvariant();

            var project = new ReClassNetProject();

            IReClassImport import;
            switch (extension)
            {
                case ReClassNetFile.FileExtension:
                    import = new ReClassNetFile(project);
                    break;
                case ReClassFile.FileExtension:
                    import = new ReClassFile(project);
                    break;
                case ReClassQtFile.FileExtension:
                    import = new ReClassQtFile(project);
                    break;
                default:
                    throw new InvalidArgumentsException($"'{path}' has the unsupported extension '{extension}'; use {ReClassNetFile.FileExtension}, {ReClassFile.FileExtension} or {ReClassQtFile.FileExtension}");
            }

            context.Project.Mutate("open_project", current => current.Classes.Count);

            //
            // The import runs into a fresh project and only ReplaceProject makes it visible, so
            // a file that fails to load leaves the open project untouched. The snapshot is still
            // taken first, because the swap itself is what an undo has to reverse.
            //
            import.Load(path, context.Host.Logger);

            if (extension == ReClassNetFile.FileExtension)
            {
                project.Path = path;
            }

            context.Host.ReplaceProject(project);

            var structured = new JObject
            {
                ["path"] = path,
                ["classCount"] = project.Classes.Count,
                ["enumCount"] = project.Enums.Count
            };

            return ToolResult.Ok(structured);
        }

        private ToolResult SaveProject(ToolArguments arguments)
        {
            var requested = arguments.OptionalString("path", null);

            if (requested != null)
            {
                requested = requested.Trim();

                if (!Path.IsPathRooted(requested))
                {
                    throw new InvalidArgumentsException($"'path' must be absolute, got '{requested}'");
                }
            }

            var structured = context.Project.Read(project =>
            {
                var target = requested ?? project.Path;

                if (string.IsNullOrEmpty(target))
                {
                    throw new ToolException(
                        "The project has never been saved, pass an absolute path",
                        $"Call save_project again with 'path' set to an absolute file name ending in {ReClassNetFile.FileExtension}.");
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    throw new ToolException(
                        $"The directory '{directory}' does not exist",
                        "Pass a path inside an existing directory.");
                }

                new ReClassNetFile(project).Save(target, context.Host.Logger);

                project.Path = target;

                return new JObject
                {
                    ["path"] = target,
                    ["classCount"] = project.Classes.Count
                };
            });

            //
            // The size is measured afterwards. Save runs inside the project hop because it
            // walks the live node tree, and the length of the file only means anything once
            // that write has finished.
            //
            structured["bytes"] = new FileInfo((string)structured["path"]).Length;

            return ToolResult.Ok(structured);
        }

        private ToolResult UndoLastChange()
        {
            if (!context.Project.Undo())
            {
                throw new ToolException(
                    "There is no recorded change to undo",
                    "Snapshots are only taken before a mutating MCP tool runs; call list_changes to see what is available.");
            }

            return ToolResult.Ok(new JObject { ["restored"] = true });
        }

        private ToolResult ListChanges()
        {
            return ToolResult.Ok(new JObject { ["snapshots"] = context.Project.Snapshots.Describe() });
        }

        private ToolResult SelectClass(ToolArguments arguments)
        {
            var uuid = arguments.Uuid("uuid");

            var structured = context.Project.Read(project =>
            {
                var node = context.Project.RequireClass(project, uuid);

                context.Host.SelectedClass = node;

                return new JObject
                {
                    ["uuid"] = node.Uuid.ToString("D"),
                    ["name"] = node.Name
                };
            });

            return ToolResult.Ok(structured);
        }

        private ToolResult GetSelection()
        {
            var structured = context.Project.Read(_ =>
            {
                var selected = context.Host.SelectedClass;

                return new JObject
                {
                    ["uuid"] = selected == null ? null : selected.Uuid.ToString("D"),
                    ["name"] = selected?.Name
                };
            });

            return ToolResult.Ok(structured);
        }

        private static HashSet<string> SelectClassFields(IReadOnlyList<string> requested)
        {
            if (requested.Count == 0)
            {
                return null;
            }

            var selected = new HashSet<string>(StringComparer.Ordinal);

            foreach (var field in requested)
            {
                var match = Array.Find(ClassFields, candidate => string.Equals(candidate, field, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    throw new InvalidArgumentsException($"'fields' contains the unknown member '{field}'; supported members are {string.Join(", ", ClassFields)}");
                }

                selected.Add(match);
            }

            return selected;
        }

        private static JObject DescribeClass(ClassNode node, HashSet<string> fields)
        {
            var item = new JObject();

            if (fields == null || fields.Contains("uuid"))
            {
                item["uuid"] = node.Uuid.ToString("D");
            }

            if (fields == null || fields.Contains("name"))
            {
                item["name"] = node.Name;
            }

            if (fields == null || fields.Contains("address"))
            {
                item["address"] = node.AddressFormula;
            }

            if (fields == null || fields.Contains("nodeCount"))
            {
                item["nodeCount"] = node.Nodes.Count;
            }

            if (fields == null || fields.Contains("memorySize"))
            {
                item["memorySize"] = node.MemorySize;
            }

            if (fields == null || fields.Contains("comment"))
            {
                item["comment"] = node.Comment;
            }

            return item;
        }

        private static JObject DescribeNode(ClassNode owner, BaseNode node, int index, int depth)
        {
            var item = new JObject
            {
                ["handle"] = NodeHandle.Format(owner, node),
                ["index"] = index,
                ["offset"] = Format.Hex(node.Offset),
                ["type"] = node.GetType().Name,
                ["name"] = node.Name,
                ["comment"] = node.Comment,
                ["memorySize"] = node.MemorySize,
                ["hidden"] = node.IsHidden
            };

            if (node is BaseWrapperArrayNode arrayNode)
            {
                item["count"] = arrayNode.Count;
                item["currentIndex"] = arrayNode.CurrentIndex;
            }

            if (node is BaseTextNode textNode)
            {
                item["length"] = textNode.Length;
            }

            if (node is BitFieldNode bitFieldNode)
            {
                item["bits"] = bitFieldNode.Bits;
            }

            if (node is EnumNode enumNode)
            {
                item["enum"] = enumNode.Enum?.Name;
            }

            //
            // A node that points at a class ends the walk and reports the uuid instead. The
            // class graph is a graph and not a tree: a pointer back to the parent class is
            // ordinary, and inlining it would recurse until the stack ran out.
            //
            if (node is BaseClassWrapperNode classWrapperNode && classWrapperNode.InnerNode is ClassNode reference)
            {
                item["reference"] = reference.Uuid.ToString("D");

                return item;
            }

            if (depth <= 1)
            {
                return item;
            }

            if (node is BaseContainerNode containerNode)
            {
                var children = new JArray();

                for (var i = 0; i < containerNode.Nodes.Count; ++i)
                {
                    children.Add(DescribeNode(owner, containerNode.Nodes[i], i, depth - 1));
                }

                item["children"] = children;

                return item;
            }

            if (node is BaseWrapperNode wrapperNode && wrapperNode.InnerNode != null)
            {
                item["children"] = new JArray { DescribeNode(owner, wrapperNode.InnerNode, 0, depth - 1) };
            }

            return item;
        }

        private static string DescribeReferences(ClassReferencedException exception)
        {
            var builder = new StringBuilder();

            foreach (var reference in exception.References)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(reference.Name);
                builder.Append(" (");
                builder.Append(reference.Uuid.ToString("D"));
                builder.Append(')');
            }

            if (builder.Length == 0)
            {
                return "another class";
            }

            return builder.ToString();
        }

        //
        // Only the syntax is checked here. ReClass.NET resolves the formula lazily while it
        // draws, so a formula that parses but points nowhere is legal and shows up as unreadable
        // memory much later. The long message is deliberate: numbers are hexadecimal without a
        // 0x, and the plausible looking game.exe+0x1f4 does not parse at all.
        //
        private static void RequireFormula(string name, string formula)
        {
            try
            {
                Parser.Parse(formula);
            }
            catch (ParseException ex)
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid address formula: {ex.Message}. Module names must be wrapped in angle brackets like <game.exe>, every number is hexadecimal (10 means 0x10), [x] dereferences, and + - * / are supported, for example [<game.exe>+0x1f4]+0x10");
            }
        }
    }
}
