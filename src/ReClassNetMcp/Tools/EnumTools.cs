using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class EnumTools
    {
        private const int MaxValues = 256;

        private readonly ToolContext context;

        public EnumTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "list_enums",
                "List enums",
                "List the enums of the open project with their flags mode, their underlying size in bytes and how many values they hold. An enum is identified by its name only, ReClass.NET has no enum uuid. Call get_enum for the value list of one enum.",
                Schema.Object(
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Items to return, default 100")),
                Schema.Object(
                    Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Enums as {name, useFlagsMode, size, valueCount}"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Enum count of the project"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListEnums(arguments)));

            registry.Add(new ToolDefinition(
                "get_enum",
                "Get enum",
                "Return one enum with its flags mode, its underlying size in bytes and every {name, value} pair it holds. Enum names are case sensitive, call list_enums when you do not know the exact name.",
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Exact enum name")),
                EnumResultSchema(),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => GetEnum(arguments)));

            registry.Add(new ToolDefinition(
                "create_enum",
                "Create enum",
                "Create a new enum in the open project. The name is the only identity an enum has, so it must not collide with an existing one. 'size' is the underlying size in bytes and must be 1, 2, 4 or 8. ReClass.NET stores the flags mode, the size and the values in a single call, so 'size' and 'useFlagsMode' are only accepted together with a non empty 'values' array; without values the enum starts as a non flags 4 byte enum and set_enum_values fills it later. Bind the enum to a node with bind_enum_node.",
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Enum name, must be unique in the project"),
                    Schema.Optional("useFlagsMode", Schema.Bool(), "True to treat the values as bit flags, default false"),
                    Schema.Optional("size", Schema.Integer(1, 8), "Underlying size in bytes: 1, 2, 4 or 8, default 4"),
                    Schema.Optional("values", ValuesSchema(), "The complete value list, at most " + MaxValues + " entries")),
                EnumResultSchema(),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => CreateEnum(arguments)));

            registry.Add(new ToolDefinition(
                "set_enum_values",
                "Set enum values",
                "Replace the complete value list of an existing enum. This is not a merge: what you pass becomes the whole enum. 'useFlagsMode' and 'size' keep their current value when omitted. Every value must fit the underlying size, signed for a normal enum and unsigned for a flags enum, and the host sorts the result by value. Changing the size changes the memory size of every node bound to this enum, so the offsets of the following fields shift.",
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Exact enum name"),
                    Schema.Optional("useFlagsMode", Schema.Bool(), "True to treat the values as bit flags, keeps the current mode when omitted"),
                    Schema.Optional("size", Schema.Integer(1, 8), "Underlying size in bytes: 1, 2, 4 or 8, keeps the current size when omitted"),
                    Schema.Required("values", ValuesSchema(), "The complete value list, at most " + MaxValues + " entries")),
                EnumResultSchema(),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => SetEnumValues(arguments)));

            registry.Add(new ToolDefinition(
                "rename_enum",
                "Rename enum",
                "Rename an enum. ReClass.NET stores enum references by name, so nodes bound to this enum keep working in the running session but lose their binding the next time the project file is loaded; the result repeats that as a 'warning' field. Re-bind affected nodes with bind_enum_node after a reload.",
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Exact current enum name"),
                    Schema.Required("newName", Schema.Text(1, 256), "New enum name, must be unique in the project")),
                Schema.Object(
                    Schema.Required("name", Schema.Text(), "The new enum name"),
                    Schema.Required("previousName", Schema.Text(), "The name before the rename"),
                    Schema.Required("useFlagsMode", Schema.Bool(), "True when the enum is a bit flags enum"),
                    Schema.Required("size", Schema.Integer(), "Underlying size in bytes"),
                    Schema.Required("valueCount", Schema.Integer(), "Number of values"),
                    Schema.Required("warning", Schema.Text(), "Why the rename breaks node references on the next project load")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => RenameEnum(arguments)));

            registry.Add(new ToolDefinition(
                "delete_enum",
                "Delete enum",
                "Delete an enum from the open project. The call fails while any enum node still references it and names the classes that do; point those nodes at another enum with bind_enum_node or change their type with change_node_type, then delete again. Recover a wrong delete with undo_last_change.",
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Exact enum name")),
                Schema.Object(
                    Schema.Required("name", Schema.Text(), "The deleted enum name"),
                    Schema.Required("deleted", Schema.Bool(), "Always true when no error was raised")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => DeleteEnum(arguments)));

            registry.Add(new ToolDefinition(
                "bind_enum_node",
                "Bind enum to node",
                "Point an existing enum node at one of the project enums. The handle must resolve to an EnumNode; create one with add_node or convert an existing node with change_node_type first. The memory size of the node follows the underlying size of the enum, so binding a differently sized enum shifts the offsets of the following fields.",
                Schema.Object(
                    Schema.Required("handle", Schema.Text(1, 128), "Node handle '<classUuid>:<i>/<j>' pointing at an EnumNode"),
                    Schema.Required("name", Schema.Text(1, 256), "Exact name of the enum to bind")),
                Schema.Object(
                    Schema.Required("handle", Schema.Text(), "Handle of the node after the mutation"),
                    Schema.Required("nodeName", Schema.Text(), "Name of the bound node"),
                    Schema.Required("memorySize", Schema.Integer(), "Memory size of the node in bytes"),
                    Schema.Required("enum", Schema.AnyObject(), "The bound enum as {name, useFlagsMode, size, valueCount}")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => BindEnumNode(arguments)));
        }

        private ToolResult ListEnums(ToolArguments arguments)
        {
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);

            var page = context.Project.Read(project =>
            {
                var total = project.Enums.Count;
                var items = new JArray();

                for (var i = offset; i < total && items.Count < limit; ++i)
                {
                    items.Add(Summarize(project.Enums[i]));
                }

                return Format.Page(items, offset, limit, total);
            });

            return ToolResult.Ok(page);
        }

        private ToolResult GetEnum(ToolArguments arguments)
        {
            var name = ReadName(arguments, "name");

            var structured = context.Project.Read(project => Detail(Require(project, name)));

            return ToolResult.Ok(structured);
        }

        private ToolResult CreateEnum(ToolArguments arguments)
        {
            var name = ReadName(arguments, "name");
            var useFlagsMode = arguments.Bool("useFlagsMode", false);
            var size = ParseSize(arguments.OptionalInteger("size", 4));
            var values = ReadValues(arguments, false);

            //
            // SetData is the only way into an EnumDescription and it takes the flags mode, the
            // size and the values in one go, so neither of the first two can be set on an enum
            // that has no values yet. Saying so beats accepting them and dropping them.
            //
            if (values.Count == 0 && (arguments.Has("size") || arguments.Has("useFlagsMode")))
            {
                throw new InvalidArgumentsException("'size' and 'useFlagsMode' are only accepted together with a non empty 'values' array, because ReClass.NET sets all three in one call");
            }

            var structured = context.Project.Mutate($"create_enum {name}", project =>
            {
                if (Find(project, name) != null)
                {
                    throw new ToolException(
                        $"An enum named '{name}' already exists",
                        "An enum name is the only enum identity and must be unique. Call set_enum_values to change the existing one, or pick another name.");
                }

                var description = new EnumDescription { Name = name };

                if (values.Count > 0)
                {
                    ApplyValues(description, useFlagsMode, size, values);
                }

                project.AddEnum(description);

                return Detail(description);
            });

            context.Host.Log(HostLogLevel.Information, $"mcp: create_enum {name} with {values.Count} value(s)");

            return ToolResult.Ok(structured);
        }

        private ToolResult SetEnumValues(ToolArguments arguments)
        {
            var name = ReadName(arguments, "name");
            var hasFlagsMode = arguments.Has("useFlagsMode");
            var useFlagsMode = arguments.Bool("useFlagsMode", false);
            var hasSize = arguments.Has("size");
            var size = hasSize ? ParseSize(arguments.Integer("size")) : EnumDescription.UnderlyingTypeSize.FourBytes;
            var values = ReadValues(arguments, true);

            var structured = context.Project.Mutate($"set_enum_values {name}", project =>
            {
                var description = Require(project, name);

                var effectiveFlagsMode = hasFlagsMode ? useFlagsMode : description.UseFlagsMode;
                var effectiveSize = hasSize ? size : description.Size;
                var sizeChanged = effectiveSize != description.Size;

                ApplyValues(description, effectiveFlagsMode, effectiveSize, values);

                if (sizeChanged)
                {
                    RefreshOffsets(project);
                }

                return Detail(description);
            });

            context.Host.Log(HostLogLevel.Information, $"mcp: set_enum_values {name} with {values.Count} value(s)");

            return ToolResult.Ok(structured);
        }

        private ToolResult RenameEnum(ToolArguments arguments)
        {
            var name = ReadName(arguments, "name");
            var newName = ReadName(arguments, "newName");

            var structured = context.Project.Mutate($"rename_enum {name} -> {newName}", project =>
            {
                var description = Require(project, name);

                if (!string.Equals(name, newName, StringComparison.Ordinal) && Find(project, newName) != null)
                {
                    throw new ToolException(
                        $"An enum named '{newName}' already exists",
                        "An enum name is the only enum identity and must be unique. Pick another name.");
                }

                description.Name = newName;

                var result = Summarize(description);
                result["previousName"] = name;
                result["warning"] = $"ReClass.NET stores enum references by name and has no enum identity, so every enum node bound to '{name}' keeps working in this session but loses its binding the next time the project is loaded. Re-bind those nodes with bind_enum_node after a reload.";

                return result;
            });

            context.Host.Log(HostLogLevel.Warning, $"mcp: rename_enum {name} -> {newName}, enum node references are stored by name and break on the next project load");

            return ToolResult.Ok(structured);
        }

        private ToolResult DeleteEnum(ToolArguments arguments)
        {
            var name = ReadName(arguments, "name");

            var structured = context.Project.Mutate($"delete_enum {name}", project =>
            {
                var description = Require(project, name);

                try
                {
                    project.RemoveEnum(description);
                }
                catch (EnumReferencedException e)
                {
                    throw new ToolException(
                        $"The enum '{name}' is still referenced by {Referencing(e)}",
                        "Point those enum nodes at another enum with bind_enum_node, or change their type with change_node_type, then delete again.");
                }

                return new JObject
                {
                    ["name"] = name,
                    ["deleted"] = true
                };
            });

            context.Host.Log(HostLogLevel.Information, $"mcp: delete_enum {name}");

            return ToolResult.Ok(structured);
        }

        private ToolResult BindEnumNode(ToolArguments arguments)
        {
            var handle = NodeHandle.Parse(arguments.String("handle"));
            var name = ReadName(arguments, "name");

            var structured = context.Project.Mutate($"bind_enum_node {handle} -> {name}", project =>
            {
                var node = context.Project.Resolve(project, handle);

                if (!(node is EnumNode enumNode))
                {
                    throw new ToolException(
                        $"Node '{handle}' is a '{node.GetType().Name}', not an EnumNode",
                        "Only an EnumNode can be bound to an enum. Convert the node with change_node_type using the type 'EnumNode', then bind again.");
                }

                var description = Require(project, name);

                var previousSize = enumNode.MemorySize;

                enumNode.ChangeEnum(description);

                if (enumNode.MemorySize != previousSize)
                {
                    RefreshOffsets(project);
                }

                return new JObject
                {
                    ["handle"] = NodeHandle.Format(context.Project.OwnerOf(enumNode), enumNode),
                    ["nodeName"] = enumNode.Name,
                    ["memorySize"] = enumNode.MemorySize,
                    ["enum"] = Summarize(description)
                };
            });

            context.Host.Log(HostLogLevel.Information, $"mcp: bind_enum_node {handle} -> {name}");

            return ToolResult.Ok(structured);
        }

        private static JObject ValuesSchema()
        {
            return Schema.ArrayOf(
                Schema.Object(
                    Schema.Required("name", Schema.Text(1, 256), "Value name"),
                    Schema.Required("value", Schema.Integer(), "Numeric value in decimal, must fit the underlying size")),
                MaxValues);
        }

        private static JObject EnumResultSchema()
        {
            return Schema.Object(
                Schema.Required("name", Schema.Text(), "Enum name"),
                Schema.Required("useFlagsMode", Schema.Bool(), "True when the enum is a bit flags enum"),
                Schema.Required("size", Schema.Integer(), "Underlying size in bytes, one of 1, 2, 4, 8"),
                Schema.Required("valueCount", Schema.Integer(), "Number of values"),
                Schema.Required("values", Schema.ArrayOf(Schema.AnyObject()), "Values as {name, value}, sorted by value"));
        }

        private static JObject Summarize(EnumDescription description)
        {
            return new JObject
            {
                ["name"] = description.Name,
                ["useFlagsMode"] = description.UseFlagsMode,
                ["size"] = (int)description.Size,
                ["valueCount"] = description.Values.Count
            };
        }

        private static JObject Detail(EnumDescription description)
        {
            var values = new JArray();

            foreach (var value in description.Values)
            {
                values.Add(new JObject
                {
                    ["name"] = value.Key,
                    ["value"] = value.Value
                });
            }

            var result = Summarize(description);
            result["values"] = values;

            return result;
        }

        private static string Referencing(EnumReferencedException exception)
        {
            var names = exception.References
                .Where(owner => owner != null)
                .Select(owner => owner.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (names.Count == 0)
            {
                return "an enum node";
            }

            return $"{names.Count} class(es): {string.Join(", ", names)}";
        }

        //
        // Ordinal on purpose. An EnumDescription carries no uuid: the host matches an enum node
        // back to its enum by name when a project is loaded, so the comparison here has to be as
        // exact as the one on load, and a rename breaks every node bound to the old name.
        //
        private static EnumDescription Find(ReClassNetProject project, string name)
        {
            return project.Enums.FirstOrDefault(description => string.Equals(description.Name, name, StringComparison.Ordinal));
        }

        private static EnumDescription Require(ReClassNetProject project, string name)
        {
            var description = Find(project, name);

            if (description == null)
            {
                throw new ToolException(
                    $"No enum named '{name}'",
                    "Enum names are case sensitive. Call list_enums for the exact names, or create_enum to add one.");
            }

            return description;
        }

        private static string ReadName(ToolArguments arguments, string argument)
        {
            var name = arguments.String(argument).Trim();

            if (name.Length == 0)
            {
                throw new InvalidArgumentsException($"'{argument}' must not be empty");
            }

            return name;
        }

        private static EnumDescription.UnderlyingTypeSize ParseSize(long value)
        {
            switch (value)
            {
                case 1:
                    return EnumDescription.UnderlyingTypeSize.OneByte;
                case 2:
                    return EnumDescription.UnderlyingTypeSize.TwoBytes;
                case 4:
                    return EnumDescription.UnderlyingTypeSize.FourBytes;
                case 8:
                    return EnumDescription.UnderlyingTypeSize.EightBytes;
                default:
                    throw new InvalidArgumentsException($"'size' must be 1, 2, 4 or 8 bytes, got {value}");
            }
        }

        private static List<KeyValuePair<string, long>> ReadValues(ToolArguments arguments, bool required)
        {
            if (!arguments.Has("values"))
            {
                if (required)
                {
                    throw new InvalidArgumentsException("Missing required argument 'values'");
                }

                return new List<KeyValuePair<string, long>>();
            }

            var entries = arguments.Objects("values");

            if (entries.Count > MaxValues)
            {
                throw new InvalidArgumentsException($"'values' must not exceed {MaxValues} entries, got {entries.Count}");
            }

            if (required && entries.Count == 0)
            {
                throw new InvalidArgumentsException("'values' must not be empty");
            }

            var values = new List<KeyValuePair<string, long>>(entries.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < entries.Count; ++i)
            {
                var entry = new ToolArguments(entries[i]);

                if (!entry.Has("name") || !entry.Has("value"))
                {
                    throw new InvalidArgumentsException($"'values[{i}]' must be an object with a 'name' and a 'value'");
                }

                var name = entry.String("name").Trim();

                if (name.Length == 0)
                {
                    throw new InvalidArgumentsException($"'values[{i}].name' must not be empty");
                }

                //
                // The host keeps the values as a plain list and takes a duplicate name without a
                // word about it, and generate_code would then emit the same identifier twice.
                //
                if (!names.Add(name))
                {
                    throw new InvalidArgumentsException($"'values' contains '{name}' more than once");
                }

                values.Add(new KeyValuePair<string, long>(name, entry.Integer("value")));
            }

            return values;
        }

        private static void ApplyValues(EnumDescription description, bool useFlagsMode, EnumDescription.UnderlyingTypeSize size, List<KeyValuePair<string, long>> values)
        {
            //
            // SetData runs Max() and Min() over the sequence, so an empty list comes back out of
            // Linq as an InvalidOperationException. The range is checked here as well because the
            // host answers an out of range value with a bare ArgumentOutOfRangeException that
            // names neither the value nor the limit it broke.
            //
            if (values.Count == 0)
            {
                throw new ToolException(
                    "An enum needs at least one value",
                    "ReClass.NET stores the flags mode, the underlying size and the values in one call, so pass a non empty 'values' array.");
            }

            var offending = Offending(useFlagsMode, size, values);

            if (offending != null)
            {
                throw new ToolException(offending, "Raise 'size' to 2, 4 or 8 bytes, drop 'useFlagsMode', or change the value.");
            }

            try
            {
                description.SetData(useFlagsMode, size, values);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ToolException(
                    $"At least one value does not fit a {(int)size} byte {(useFlagsMode ? "flags " : string.Empty)}enum",
                    "Raise 'size' to 2, 4 or 8 bytes, drop 'useFlagsMode', or change the values.");
            }
        }

        private static string Offending(bool useFlagsMode, EnumDescription.UnderlyingTypeSize size, List<KeyValuePair<string, long>> values)
        {
            if (useFlagsMode)
            {
                var maximum = ulong.MaxValue;

                switch (size)
                {
                    case EnumDescription.UnderlyingTypeSize.OneByte:
                        maximum = byte.MaxValue;
                        break;
                    case EnumDescription.UnderlyingTypeSize.TwoBytes:
                        maximum = ushort.MaxValue;
                        break;
                    case EnumDescription.UnderlyingTypeSize.FourBytes:
                        maximum = uint.MaxValue;
                        break;
                }

                foreach (var value in values)
                {
                    if (unchecked((ulong)value.Value) > maximum)
                    {
                        return $"The value '{value.Key}' = {value.Value} does not fit a {(int)size} byte flags enum, which holds 0 to {maximum}";
                    }
                }

                return null;
            }

            var minimum = long.MinValue;
            var signedMaximum = long.MaxValue;

            switch (size)
            {
                case EnumDescription.UnderlyingTypeSize.OneByte:
                    minimum = sbyte.MinValue;
                    signedMaximum = sbyte.MaxValue;
                    break;
                case EnumDescription.UnderlyingTypeSize.TwoBytes:
                    minimum = short.MinValue;
                    signedMaximum = short.MaxValue;
                    break;
                case EnumDescription.UnderlyingTypeSize.FourBytes:
                    minimum = int.MinValue;
                    signedMaximum = int.MaxValue;
                    break;
            }

            foreach (var value in values)
            {
                if (value.Value < minimum || value.Value > signedMaximum)
                {
                    return $"The value '{value.Key}' = {value.Value} does not fit a {(int)size} byte enum, which holds {minimum} to {signedMaximum}";
                }
            }

            return null;
        }

        //
        // The memory size of an EnumNode follows the underlying size of its enum, and the host
        // recomputes offsets only when a container is updated. Every class has to be walked after
        // a size change or the offsets of the fields behind the enum stay where they were.
        //
        private static void RefreshOffsets(ReClassNetProject project)
        {
            foreach (var owner in project.Classes)
            {
                owner.UpdateOffsets();
            }
        }
    }
}
