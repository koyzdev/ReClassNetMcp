using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.CodeGenerator;
using ReClassNET.Logger;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNetMcp.Abstractions;

namespace ReClassNetMcp.Tools
{
    internal sealed class CodeTools
    {
        private const int MaxClasses = 256;

        private const int MaxMessages = 64;

        private readonly ToolContext context;

        public CodeTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "generate_code",
                "Generate code",
                "Generate C++ or C# source for the classes of the open project. Pass 'uuids' to generate a subset, otherwise every class is generated; call list_classes for the uuids. The project enums are emitted as enum declarations unless 'includeEnums' is false. The C++ generator uses the project type mapping, which get_type_mapping reports. The C# generator does not cover every node type: nodes it cannot map are skipped and every skipped type is reported in 'warnings', so read that field before trusting C# output. With 'path' the code is written to that absolute file as UTF-8 and only the byte count comes back, which is the right choice for a large project.",
                Schema.Object(
                    Schema.Optional("language", Schema.Enum("cpp", "csharp"), "Target language, default cpp"),
                    Schema.Optional("uuids", Schema.ArrayOf(Schema.Uuid(), MaxClasses), "Class uuids to generate, every class of the project when omitted"),
                    Schema.Optional("includeEnums", Schema.Bool(), "Emit the project enums as well, default true"),
                    Schema.Optional("path", Schema.Text(1, 4096), "Absolute file path to write the code to instead of returning it inline")),
                Schema.Object(
                    Schema.Required("language", Schema.Text(), "The generated language, cpp or csharp"),
                    Schema.Required("classCount", Schema.Integer(), "Number of classes handed to the generator"),
                    Schema.Optional("enumCount", Schema.Integer(), "Number of enums handed to the generator"),
                    Schema.Optional("code", Schema.Text(), "The generated code, absent when 'path' was given"),
                    Schema.Optional("path", Schema.Text(), "The written file, absent when the code is returned inline"),
                    Schema.Optional("bytes", Schema.Integer(), "Bytes written to 'path'"),
                    Schema.Optional("warnings", Schema.ArrayOf(Schema.AnyObject()), "Generator messages as {level, message}, for example a skipped node type")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => GenerateCode(arguments, token)));

            registry.Add(new ToolDefinition(
                "get_type_mapping",
                "Get C++ type mapping",
                "Report the C++ type names the code generator uses for the primitive node types of the open project, as a map of mapping name to type name. TypeInt32 is the name emitted for an Int32Node, TypeUtf8Text for a Utf8TextNode, and so on. Read it before generate_code with language cpp when the output must match the type names of an existing code base.",
                Schema.Object(),
                Schema.Map(Schema.Text()),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => GetTypeMapping()));
        }

        private ToolResult GenerateCode(ToolArguments arguments, CancellationToken token)
        {
            var language = arguments.OptionalString("language", "cpp").Trim().ToLowerInvariant();

            if (language != "cpp" && language != "csharp")
            {
                throw new InvalidArgumentsException($"'language' must be 'cpp' or 'csharp', got '{language}'");
            }

            var includeEnums = arguments.Bool("includeEnums", true);
            var path = arguments.OptionalString("path", null);

            if (path != null)
            {
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    throw new InvalidArgumentsException($"'path' contains characters which are not allowed in a file path: '{path}'");
                }

                if (!Path.IsPathRooted(path))
                {
                    throw new InvalidArgumentsException($"'path' must be absolute, got '{path}'");
                }

                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    throw new InvalidArgumentsException($"The directory of 'path' does not exist: '{directory}'");
                }
            }

            var uuids = ReadUuids(arguments);

            var request = context.Project.Read(project =>
            {
                var classes = new List<ClassNode>();

                if (uuids.Count == 0)
                {
                    classes.AddRange(project.Classes);
                }
                else
                {
                    foreach (var uuid in uuids)
                    {
                        classes.Add(context.Project.RequireClass(project, uuid));
                    }
                }

                var enums = new List<EnumDescription>();

                if (includeEnums)
                {
                    enums.AddRange(project.Enums);
                }

                ICodeGenerator generator;

                if (language == "cpp")
                {
                    generator = new CppCodeGenerator(project.TypeMapping);
                }
                else
                {
                    generator = new CSharpCodeGenerator();
                }

                return new GenerationRequest
                {
                    Generator = generator,
                    Classes = classes,
                    Enums = enums
                };
            });

            token.ThrowIfCancellationRequested();

            //
            // The generator gets a private logger and not context.Host.Logger. The host logger is
            // a GuiLogger that puts its entries into a window, and appending to it from a request
            // thread touches that window from the wrong thread. Collecting the messages here also
            // gets them into the response, which is where the caller can act on them.
            //
            var logger = new CollectingLogger();
            var code = request.Generator.GenerateCode(request.Classes, request.Enums, logger);

            var structured = new JObject
            {
                ["language"] = language,
                ["classCount"] = request.Classes.Count,
                ["enumCount"] = request.Enums.Count
            };

            if (path == null)
            {
                structured["code"] = code;
            }
            else
            {
                long written;

                try
                {
                    File.WriteAllText(path, code, new UTF8Encoding(false));
                    written = new FileInfo(path).Length;
                }
                catch (UnauthorizedAccessException e)
                {
                    throw new ToolException($"Could not write '{path}': {e.Message}", "Pick a path the ReClass.NET process may write to.");
                }
                catch (IOException e)
                {
                    throw new ToolException($"Could not write '{path}': {e.Message}", "Pick a path the ReClass.NET process may write to.");
                }

                structured["path"] = path;
                structured["bytes"] = written;

                context.Host.Log(HostLogLevel.Information, $"mcp: generate_code {language} -> {path} ({written} bytes)");
            }

            if (logger.Entries.Count > 0)
            {
                var warnings = new JArray();

                foreach (var entry in logger.Entries)
                {
                    warnings.Add(entry);
                }

                structured["warnings"] = warnings;
            }

            return ToolResult.Ok(structured);
        }

        private ToolResult GetTypeMapping()
        {
            var structured = context.Project.Read(project =>
            {
                var mapping = new JObject();

                //
                // CppTypeMapping has no enumeration of its own, so the mapping is read by
                // reflection. The property names are the contract: they are what the C++ generator
                // looks up, so they go over the wire unchanged.
                //
                var properties = typeof(CppTypeMapping)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.PropertyType == typeof(string) && property.CanRead && property.GetIndexParameters().Length == 0)
                    .OrderBy(property => property.Name, StringComparer.Ordinal);

                foreach (var property in properties)
                {
                    mapping[property.Name] = (string)property.GetValue(project.TypeMapping) ?? string.Empty;
                }

                return mapping;
            });

            return ToolResult.Ok(structured);
        }

        private static List<Guid> ReadUuids(ToolArguments arguments)
        {
            var texts = arguments.Strings("uuids");

            if (texts.Count > MaxClasses)
            {
                throw new InvalidArgumentsException($"'uuids' must not exceed {MaxClasses} entries, got {texts.Count}");
            }

            var uuids = new List<Guid>(texts.Count);
            var seen = new HashSet<Guid>();

            foreach (var text in texts)
            {
                if (!Guid.TryParse(text, out var uuid))
                {
                    throw new InvalidArgumentsException($"'uuids' contains a value which is not a uuid: '{text}'");
                }

                if (seen.Add(uuid))
                {
                    uuids.Add(uuid);
                }
            }

            return uuids;
        }

        private sealed class GenerationRequest
        {
            public ICodeGenerator Generator { get; set; }

            public List<ClassNode> Classes { get; set; }

            public List<EnumDescription> Enums { get; set; }
        }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<JObject> entries = new List<JObject>();

            public event NewLogEntryEventHandler NewLogEntry;

            public IReadOnlyList<JObject> Entries => entries;

            public void Log(Exception ex)
            {
                Log(LogLevel.Error, ex.Message);
            }

            public void Log(LogLevel level, string message)
            {
                //
                // The list is capped but the event is always raised. A C# generation over a large
                // project logs one message per node type it cannot map, and the response should
                // not turn into a wall of them.
                //
                if (entries.Count < MaxMessages)
                {
                    entries.Add(new JObject
                    {
                        ["level"] = level.ToString(),
                        ["message"] = message
                    });
                }

                NewLogEntry?.Invoke(level, message, null);
            }
        }
    }
}
