using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.AddressParser;
using ReClassNET.Extensions;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class MemoryTools
    {
        //
        // The caps exist because a model will eventually ask for size = 0xFFFFFFFF. Every read
        // allocates its own buffer, so MaxSingleRead bounds one window and MaxTotalRead bounds
        // what a single batch may allocate before the first byte is fetched.
        //
        private const int MaxSingleRead = 1 << 20;

        private const int MaxTotalRead = 4 << 20;

        private const int MaxBatch = 256;

        private const int MaxStringLength = 4096;

        private const int MaxInstructions = 512;

        private const int MaxElements = 1024;

        private static readonly string[] ValueTypes =
        {
            "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double", "bool", "pointer"
        };

        private readonly ToolContext context;

        public MemoryTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "read_memory",
                "Read raw memory",
                "Read raw bytes from the attached process. Every entry takes either an 'address' (hexadecimal) or a 'formula' (ReClass.NET syntax) plus a 'size' in bytes. Pass a 'reads' array to fetch several windows in one call, which is the efficient way to walk a structure. A failed read reports success=false with an error instead of returning zero bytes.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Start address, hexadecimal, with or without 0x"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula, e.g. <module.exe>+0x1f4"),
                    Schema.Optional("size", Schema.Integer(1, MaxSingleRead), "Bytes to read for the single form"),
                    Schema.Optional("reads", Schema.ArrayOf(Schema.Object(
                        Schema.Optional("address", Schema.Address(), "Start address"),
                        Schema.Optional("formula", Schema.Formula(), "Address formula"),
                        Schema.Required("size", Schema.Integer(1, MaxSingleRead), "Bytes to read")), MaxBatch), "A batch of reads")),
                Schema.Object(Schema.Required("reads", Schema.ArrayOf(Schema.AnyObject()), "One entry per read: {address, size, hex, base64, success} or {address, size, success, error}")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ReadMemory(arguments, token)));

            registry.Add(new ToolDefinition(
                "read_typed",
                "Read typed values",
                "Read one or more typed values from the attached process. 'type' is one of int8 uint8 int16 uint16 int32 uint32 int64 uint64 float double bool pointer. 'count' reads consecutive elements of that type. Pointer values additionally report the module and section they point into, which is how you tell a real pointer from noise.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Address, hexadecimal"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula"),
                    Schema.Optional("type", Schema.Enum(ValueTypes), "Value type for the single form"),
                    Schema.Optional("count", Schema.Integer(1, MaxElements), "Consecutive elements to read, default 1"),
                    Schema.Optional("reads", Schema.ArrayOf(Schema.Object(
                        Schema.Optional("address", Schema.Address(), "Address"),
                        Schema.Optional("formula", Schema.Formula(), "Address formula"),
                        Schema.Required("type", Schema.Enum(ValueTypes), "Value type"),
                        Schema.Optional("count", Schema.Integer(1, MaxElements), "Consecutive elements, default 1")), MaxBatch), "A batch of typed reads")),
                Schema.Object(Schema.Required("reads", Schema.ArrayOf(Schema.AnyObject()), "One entry per read: {address, type, count, values, raw}")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ReadTyped(arguments, token)));

            registry.Add(new ToolDefinition(
                "read_string",
                "Read string",
                "Read a text value from the attached process. 'encoding' is utf8, utf16 or utf32; utf16 is the usual choice for Windows wide strings. By default the value is cut at the first null character.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Address, hexadecimal"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula"),
                    Schema.Optional("encoding", Schema.Enum("utf8", "utf16", "utf32"), "Character encoding, default utf8"),
                    Schema.Optional("length", Schema.Integer(1, MaxStringLength), "Maximum characters to read, default 256"),
                    Schema.Optional("untilNull", Schema.Bool(), "Cut at the first null character, default true")),
                Schema.Object(Schema.Required("address", Schema.Text(), "Resolved address"),
                    Schema.Required("encoding", Schema.Text(), "Applied encoding"),
                    Schema.Required("length", Schema.Integer(), "Character count of the returned value"),
                    Schema.Required("value", Schema.Text(), "The string")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ReadString(arguments)));

            registry.Add(new ToolDefinition(
                "write_memory",
                "Write raw memory",
                "Write raw bytes into the attached process. Supply the payload as 'hex' or 'base64'. The bytes that were there before the write are returned as 'previous', so you can revert by writing them back. A rejected write is reported as an error, never as a silent success.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Target address, hexadecimal"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula"),
                    Schema.Optional("hex", Schema.Text(), "Payload as a hexadecimal string"),
                    Schema.Optional("base64", Schema.Text(), "Payload as base64")),
                Schema.Object(Schema.Required("address", Schema.Text(), "Resolved address"),
                    Schema.Required("size", Schema.Integer(), "Bytes written"),
                    Schema.Required("previous", Schema.AnyObject(), "The overwritten bytes as {hex, base64}")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => WriteMemory(arguments)));

            registry.Add(new ToolDefinition(
                "write_typed",
                "Write typed value",
                "Write a typed value into the attached process. Use 'value' for a single element or 'values' for consecutive elements of the same type. The overwritten bytes are returned as 'previous' so the change can be reverted.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Target address, hexadecimal"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula"),
                    Schema.Required("type", Schema.Enum(ValueTypes), "Value type"),
                    Schema.Optional("value", Schema.Text(), "Single value, given as a number or a decimal string"),
                    Schema.Optional("values", Schema.ArrayOf(Schema.Text(), MaxElements), "Consecutive values of the same type")),
                Schema.Object(Schema.Required("address", Schema.Text(), "Resolved address"),
                    Schema.Required("type", Schema.Text(), "Applied type"),
                    Schema.Required("count", Schema.Integer(), "Elements written"),
                    Schema.Required("size", Schema.Integer(), "Bytes written"),
                    Schema.Required("previous", Schema.AnyObject(), "The overwritten bytes as {hex, base64}")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => WriteTyped(arguments)));

            registry.Add(new ToolDefinition(
                "find_pattern",
                "Find byte pattern",
                "Search a bounded region of the attached process for an IDA style byte pattern such as '48 8B 05 ?? ?? ?? ??'. A scope is required: pass 'module', 'section', or an explicit 'address' plus 'size'. The explicit range is read into memory in one block, so keep it bounded.",
                Schema.Object(
                    Schema.Required("pattern", Schema.Text(), "Byte pattern, ?? and nibble wildcards allowed"),
                    Schema.Optional("module", Schema.Text(), "Restrict the search to this module"),
                    Schema.Optional("section", Schema.Text(), "Restrict the search to this section name"),
                    Schema.Optional("address", Schema.Address(), "Explicit range start"),
                    Schema.Optional("size", Schema.Integer(1, 256 << 20), "Explicit range size in bytes")),
                Schema.Object(Schema.Required("found", Schema.Bool(), "Whether the pattern was found"),
                    Schema.Optional("address", Schema.Text(), "Match address"),
                    Schema.Optional("module", Schema.Text(), "Module containing the match"),
                    Schema.Optional("section", Schema.Text(), "Section containing the match")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => FindPattern(arguments)));

            registry.Add(new ToolDefinition(
                "disassemble",
                "Disassemble code",
                "Disassemble instructions at an address in the attached process. Pass 'count' for a number of instructions or 'size' for a byte window.",
                Schema.Object(
                    Schema.Optional("address", Schema.Address(), "Address, hexadecimal"),
                    Schema.Optional("formula", Schema.Formula(), "Address formula"),
                    Schema.Optional("count", Schema.Integer(1, MaxInstructions), "Instructions to decode, default 32"),
                    Schema.Optional("size", Schema.Integer(1, MaxSingleRead), "Byte window to decode instead of an instruction count")),
                Schema.Object(Schema.Required("address", Schema.Text(), "Start address"),
                    Schema.Required("instructions", Schema.ArrayOf(Schema.AnyObject()), "Decoded instructions as {address, length, hex, text}")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => Disassemble(arguments)));

            registry.Add(new ToolDefinition(
                "dump_region",
                "Dump memory to file",
                "Dump a module, a section or an explicit address range from the attached process to a file. A module dump patches the PE section headers so the file can be opened by a disassembler, but imports are not reconstructed.",
                Schema.Object(
                    Schema.Required("path", Schema.Text(), "Absolute output file path, its directory must exist"),
                    Schema.Optional("module", Schema.Text(), "Module to dump"),
                    Schema.Optional("section", Schema.Text(), "Section name to dump"),
                    Schema.Optional("address", Schema.Address(), "Explicit range start"),
                    Schema.Optional("size", Schema.Integer(1, 256 << 20), "Explicit range size in bytes")),
                Schema.Object(Schema.Required("path", Schema.Text(), "Written file"),
                    Schema.Required("kind", Schema.Text(), "module, section or raw"),
                    Schema.Required("bytes", Schema.Integer(), "File size in bytes")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => DumpRegion(arguments)));
        }

        private ToolResult ReadMemory(ToolArguments arguments, CancellationToken token)
        {
            var process = context.RequireProcess();
            var entries = CollectEntries(arguments, "reads", entry => entry["size"] != null || entry["address"] != null || entry["formula"] != null);

            var total = 0L;
            var results = new JArray();

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                var size = ReadSize(entry);
                total += size;

                if (total > MaxTotalRead)
                {
                    throw new InvalidArgumentsException($"The batch requests {total} bytes, which exceeds the {MaxTotalRead} byte total limit");
                }

                IntPtr address;
                try
                {
                    address = ResolveTarget(process, entry);
                }
                catch (ParseException ex)
                {
                    results.Add(new JObject { ["success"] = false, ["error"] = ex.Message });
                    continue;
                }

                //
                // ReadRemoteMemory(address, size) swallows the failure bool and hands back a
                // zero filled buffer, which nothing can tell apart from a page of real zeroes.
                // The IntoBuffer overload keeps the bool, so a dead region is answered with
                // success=false instead of being served as data.
                //
                var buffer = new byte[size];
                if (!process.ReadRemoteMemoryIntoBuffer(address, ref buffer, 0, size))
                {
                    results.Add(new JObject
                    {
                        ["success"] = false,
                        ["address"] = Format.Hex(address),
                        ["size"] = size,
                        ["error"] = "The read failed, the region is not readable or the process died"
                    });

                    continue;
                }

                var payload = Format.Payload(address, buffer);
                payload["success"] = true;
                results.Add(payload);
            }

            return ToolResult.Ok(new JObject { ["reads"] = results });
        }

        private ToolResult ReadTyped(ToolArguments arguments, CancellationToken token)
        {
            var process = context.RequireProcess();
            var entries = CollectEntries(arguments, "reads", entry => entry["type"] != null);

            var results = new JArray();

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                var type = RequireString(entry, "type").ToLowerInvariant();
                var count = ReadCount(entry);
                var elementSize = SizeOf(type);

                IntPtr address;
                try
                {
                    address = ResolveTarget(process, entry);
                }
                catch (ParseException ex)
                {
                    results.Add(new JObject { ["success"] = false, ["error"] = ex.Message });
                    continue;
                }

                var buffer = new byte[elementSize * count];
                if (!process.ReadRemoteMemoryIntoBuffer(address, ref buffer, 0, buffer.Length))
                {
                    results.Add(new JObject
                    {
                        ["success"] = false,
                        ["address"] = Format.Hex(address),
                        ["type"] = type,
                        ["error"] = "The read failed, the region is not readable or the process died"
                    });

                    continue;
                }

                var values = new JArray();
                for (var i = 0; i < count; ++i)
                {
                    var elementAddress = address + i * elementSize;
                    values.Add(Decode(process, type, elementAddress, buffer, i * elementSize));
                }

                results.Add(new JObject
                {
                    ["success"] = true,
                    ["address"] = Format.Hex(address),
                    ["type"] = type,
                    ["count"] = count,
                    ["values"] = values,
                    ["raw"] = new JObject
                    {
                        ["hex"] = Format.HexBytes(buffer),
                        ["base64"] = Convert.ToBase64String(buffer)
                    }
                });
            }

            return ToolResult.Ok(new JObject { ["reads"] = results });
        }

        private ToolResult ReadString(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var address = ResolveTarget(process, arguments);
            var encodingName = arguments.OptionalString("encoding", "utf8").ToLowerInvariant();
            var length = arguments.Count("length", 256, MaxStringLength);
            var untilNull = arguments.Bool("untilNull", true);

            var encoding = Encoding(encodingName);

            var value = untilNull
                ? process.ReadRemoteStringUntilFirstNullCharacter(address, encoding, length)
                : process.ReadRemoteString(address, encoding, length);

            return ToolResult.Ok(new JObject
            {
                ["address"] = Format.Hex(address),
                ["encoding"] = encodingName,
                ["length"] = value?.Length ?? 0,
                ["value"] = value
            });
        }

        private ToolResult WriteMemory(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var address = ResolveTarget(process, arguments);
            var data = arguments.Data();

            if (data.Length == 0)
            {
                throw new InvalidArgumentsException("The payload is empty");
            }

            if (data.Length > MaxSingleRead)
            {
                throw new InvalidArgumentsException($"The payload of {data.Length} bytes exceeds the {MaxSingleRead} byte limit");
            }

            var previous = Snapshot(process, address, data.Length);

            //
            // The typed WriteRemoteMemory extensions return void and throw the result away, so
            // the byte[] overload is the only one that can say whether the write landed. The
            // old bytes are read first so the caller can put them back.
            //
            if (!process.WriteRemoteMemory(address, data))
            {
                throw new ToolException(
                    $"The write of {data.Length} bytes to {Format.Hex(address)} failed",
                    "The region may be read only or guarded; check list_sections for its protection.");
            }

            context.Host.Log(HostLogLevel.Information, $"mcp: write_memory {Format.Hex(address)} {data.Length} bytes");

            return ToolResult.Ok(new JObject
            {
                ["address"] = Format.Hex(address),
                ["size"] = data.Length,
                ["previous"] = previous
            });
        }

        private ToolResult WriteTyped(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var address = ResolveTarget(process, arguments);
            var type = arguments.String("type").ToLowerInvariant();

            var literals = new List<string>();
            if (arguments.Has("value"))
            {
                literals.Add(Literal(arguments, "value"));
            }

            literals.AddRange(arguments.Strings("values"));

            if (literals.Count == 0)
            {
                throw new InvalidArgumentsException("Provide 'value' or a non empty 'values' array");
            }

            if (literals.Count > MaxElements)
            {
                throw new InvalidArgumentsException($"'values' must not exceed {MaxElements} entries");
            }

            //
            // The elements are packed into one buffer and written with a single call, again
            // because only the byte[] overload reports success. Per element writes would also
            // leave the array half updated once the region turns out to be read only.
            //
            var payload = new List<byte>(literals.Count * SizeOf(type));
            foreach (var literal in literals)
            {
                payload.AddRange(Encode(process, type, literal));
            }

            var data = payload.ToArray();
            var previous = Snapshot(process, address, data.Length);

            if (!process.WriteRemoteMemory(address, data))
            {
                throw new ToolException(
                    $"The write of {data.Length} bytes to {Format.Hex(address)} failed",
                    "The region may be read only or guarded; check list_sections for its protection.");
            }

            context.Host.Log(HostLogLevel.Information, $"mcp: write_typed {type} x{literals.Count} to {Format.Hex(address)}");

            return ToolResult.Ok(new JObject
            {
                ["address"] = Format.Hex(address),
                ["type"] = type,
                ["count"] = literals.Count,
                ["size"] = data.Length,
                ["previous"] = previous
            });
        }

        private ToolResult FindPattern(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var patternText = arguments.String("pattern");

            BytePattern pattern;
            try
            {
                pattern = BytePattern.Parse(patternText);
            }
            catch (Exception ex)
            {
                throw new InvalidArgumentsException($"'pattern' is not a valid byte pattern: {ex.Message}");
            }

            var moduleName = arguments.OptionalString("module", null);
            var sectionName = arguments.OptionalString("section", null);

            IntPtr result;

            if (moduleName != null)
            {
                var module = process.GetModuleByName(moduleName);
                if (module == null)
                {
                    throw new ToolException($"No module named '{moduleName}'", "Call list_modules to see the loaded modules.");
                }

                result = PatternScanner.FindPattern(pattern, process, module);
            }
            else if (sectionName != null)
            {
                var section = process.Sections.FirstOrDefault(candidate => string.Equals(candidate.Name, sectionName, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                {
                    throw new ToolException($"No section named '{sectionName}'", "Call list_sections to see the sections.");
                }

                result = PatternScanner.FindPattern(pattern, process, section);
            }
            else if (arguments.Has("address") || arguments.Has("formula"))
            {
                var start = ResolveTarget(process, arguments);
                var size = arguments.Count("size", 0, 256 << 20);

                if (size == 0)
                {
                    throw new InvalidArgumentsException("'size' is required when searching an explicit address range");
                }

                result = PatternScanner.FindPattern(pattern, process, start, size);
            }
            else
            {
                throw new InvalidArgumentsException("Provide a scope: 'module', 'section', or 'address' plus 'size'");
            }

            if (result == IntPtr.Zero)
            {
                return ToolResult.Ok(new JObject { ["found"] = false });
            }

            return ToolResult.Ok(new JObject
            {
                ["found"] = true,
                ["address"] = Format.Hex(result),
                ["module"] = process.GetModuleToPointer(result)?.Name,
                ["section"] = process.GetSectionToPointer(result)?.Name
            });
        }

        private ToolResult Disassemble(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var address = ResolveTarget(process, arguments);

            var count = arguments.Count("count", 32, MaxInstructions);
            var size = arguments.Count("size", 0, MaxSingleRead);

            if (size == 0)
            {
                size = Math.Min(MaxSingleRead, count * Disassembler.MaximumInstructionLength);
            }

            var disassembler = new Disassembler(process.CoreFunctions);
            var instructions = disassembler.RemoteDisassembleCode(process, address, size, count);

            var items = new JArray();
            foreach (var instruction in instructions)
            {
                items.Add(new JObject
                {
                    ["address"] = Format.Hex(instruction.Address),
                    ["length"] = instruction.Length,
                    ["hex"] = Format.HexBytes(Trim(instruction.Data, instruction.Length)),
                    ["text"] = instruction.Instruction
                });
            }

            return ToolResult.Ok(new JObject
            {
                ["address"] = Format.Hex(address),
                ["instructions"] = items
            });
        }

        //
        // InstructionData.Data is marshalled as a fixed 15 byte array, so every instruction
        // carries 15 bytes no matter how long it really is. Without this the hex of a two byte
        // instruction shows thirteen bytes of the instructions after it.
        //
        private static byte[] Trim(byte[] data, int length)
        {
            if (data == null)
            {
                return new byte[0];
            }

            if (length <= 0 || length >= data.Length)
            {
                return data;
            }

            var trimmed = new byte[length];
            Buffer.BlockCopy(data, 0, trimmed, 0, length);

            return trimmed;
        }

        private ToolResult DumpRegion(ToolArguments arguments)
        {
            var process = context.RequireProcess();
            var path = arguments.String("path");

            if (!Path.IsPathRooted(path))
            {
                throw new InvalidArgumentsException($"'path' must be absolute, got '{path}'");
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new InvalidArgumentsException($"The directory of 'path' does not exist: '{directory}'");
            }

            var moduleName = arguments.OptionalString("module", null);
            var sectionName = arguments.OptionalString("section", null);
            string kind;

            using (var stream = File.Create(path))
            {
                if (moduleName != null)
                {
                    var module = process.GetModuleByName(moduleName);
                    if (module == null)
                    {
                        throw new ToolException($"No module named '{moduleName}'", "Call list_modules to see the loaded modules.");
                    }

                    Dumper.DumpModule(process, module, stream);
                    kind = "module";
                }
                else if (sectionName != null)
                {
                    var section = process.Sections.FirstOrDefault(candidate => string.Equals(candidate.Name, sectionName, StringComparison.OrdinalIgnoreCase));
                    if (section == null)
                    {
                        throw new ToolException($"No section named '{sectionName}'", "Call list_sections to see the sections.");
                    }

                    Dumper.DumpSection(process, section, stream);
                    kind = "section";
                }
                else if (arguments.Has("address") || arguments.Has("formula"))
                {
                    var start = ResolveTarget(process, arguments);
                    var size = arguments.Count("size", 0, 256 << 20);

                    if (size == 0)
                    {
                        throw new InvalidArgumentsException("'size' is required when dumping an explicit address range");
                    }

                    Dumper.DumpRaw(process, start, size, stream);
                    kind = "raw";
                }
                else
                {
                    throw new InvalidArgumentsException("Provide what to dump: 'module', 'section', or 'address' plus 'size'");
                }
            }

            var written = new FileInfo(path).Length;

            context.Host.Log(HostLogLevel.Information, $"mcp: dump_region {kind} -> {path} ({written} bytes)");

            return ToolResult.Ok(new JObject
            {
                ["path"] = path,
                ["kind"] = kind,
                ["bytes"] = written
            });
        }

        private IReadOnlyList<JObject> CollectEntries(ToolArguments arguments, string batchName, Func<JObject, bool> isComplete)
        {
            if (arguments.Has(batchName))
            {
                var entries = arguments.Objects(batchName);

                if (entries.Count == 0)
                {
                    throw new InvalidArgumentsException($"'{batchName}' must not be empty");
                }

                if (entries.Count > MaxBatch)
                {
                    throw new InvalidArgumentsException($"'{batchName}' must not exceed {MaxBatch} entries, got {entries.Count}");
                }

                return entries;
            }

            var single = new JObject();
            foreach (var name in new[] { "address", "formula", "size", "type", "count" })
            {
                var value = arguments.Raw(name);
                if (value != null)
                {
                    single[name] = value.DeepClone();
                }
            }

            if (!isComplete(single))
            {
                throw new InvalidArgumentsException($"Provide '{batchName}', or the single form arguments");
            }

            return new[] { single };
        }

        private static string Literal(ToolArguments arguments, string name)
        {
            var text = arguments.OptionalString(name, null);
            if (text != null)
            {
                return text;
            }

            return arguments.Integer(name).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private IntPtr ResolveTarget(IProcessReader process, ToolArguments arguments)
        {
            if (arguments.Has("address"))
            {
                return arguments.Address("address");
            }

            var formula = arguments.OptionalString("formula", null);
            if (formula == null)
            {
                throw new InvalidArgumentsException("Provide either 'address' or 'formula'");
            }

            return ResolveFormula(process, formula);
        }

        private IntPtr ResolveTarget(IProcessReader process, JObject entry)
        {
            var address = entry["address"];
            if (address != null && address.Type != JTokenType.Null)
            {
                return ToolArguments.ParseAddress("address", address.Type == JTokenType.String ? (string)address : address.ToString());
            }

            var formula = entry["formula"];
            if (formula == null || formula.Type == JTokenType.Null)
            {
                throw new InvalidArgumentsException("Each entry needs either 'address' or 'formula'");
            }

            return ResolveFormula(process, (string)formula);
        }

        private IntPtr ResolveFormula(IProcessReader process, string formula)
        {
            try
            {
                return AddressResolver.Resolve(process, formula);
            }
            catch (ParseException ex)
            {
                throw new InvalidArgumentsException(
                    $"'{formula}' is not a valid address formula: {ex.Message}. Module names must be wrapped in angle brackets and every number is hexadecimal.");
            }
        }

        //
        // A failed read is reported as readable=false and not as a buffer of zeroes. This is
        // the payload a caller writes back to revert, and an all zero revert would destroy the
        // very bytes it was supposed to restore.
        //
        private static JObject Snapshot(RemoteProcess process, IntPtr address, int size)
        {
            var buffer = new byte[size];

            if (!process.ReadRemoteMemoryIntoBuffer(address, ref buffer, 0, size))
            {
                return new JObject { ["readable"] = false };
            }

            return new JObject
            {
                ["readable"] = true,
                ["hex"] = Format.HexBytes(buffer),
                ["base64"] = Convert.ToBase64String(buffer)
            };
        }

        private static int ReadSize(JObject entry)
        {
            var size = entry["size"];
            if (size == null || size.Type == JTokenType.Null)
            {
                throw new InvalidArgumentsException("Each read needs a 'size'");
            }

            var value = size.Type == JTokenType.Integer ? (long)size : long.Parse((string)size, System.Globalization.CultureInfo.InvariantCulture);

            if (value <= 0)
            {
                throw new InvalidArgumentsException("'size' must be positive");
            }

            if (value > MaxSingleRead)
            {
                throw new InvalidArgumentsException($"'size' must not exceed {MaxSingleRead} bytes, got {value}");
            }

            return (int)value;
        }

        private static int ReadCount(JObject entry)
        {
            var count = entry["count"];
            if (count == null || count.Type == JTokenType.Null)
            {
                return 1;
            }

            var value = count.Type == JTokenType.Integer ? (long)count : long.Parse((string)count, System.Globalization.CultureInfo.InvariantCulture);

            if (value <= 0)
            {
                throw new InvalidArgumentsException("'count' must be positive");
            }

            if (value > MaxElements)
            {
                throw new InvalidArgumentsException($"'count' must not exceed {MaxElements}, got {value}");
            }

            return (int)value;
        }

        private static string RequireString(JObject entry, string name)
        {
            var token = entry[name];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidArgumentsException($"Each entry needs a string '{name}'");
            }

            return (string)token;
        }

        private static Encoding Encoding(string name)
        {
            switch (name)
            {
                case "utf8":
                    return System.Text.Encoding.UTF8;

                case "utf16":
                    return System.Text.Encoding.Unicode;

                case "utf32":
                    return System.Text.Encoding.UTF32;

                default:
                    throw new InvalidArgumentsException($"'encoding' must be utf8, utf16 or utf32, not '{name}'");
            }
        }

        private static int SizeOf(string type)
        {
            switch (type)
            {
                case "int8":
                case "uint8":
                case "bool":
                    return 1;

                case "int16":
                case "uint16":
                    return 2;

                case "int32":
                case "uint32":
                case "float":
                    return 4;

                case "int64":
                case "uint64":
                case "double":
                    return 8;

                case "pointer":
                    return IntPtr.Size;

                default:
                    throw new InvalidArgumentsException($"'type' must be one of {string.Join(", ", ValueTypes)}, not '{type}'");
            }
        }

        private static JToken Decode(RemoteProcess process, string type, IntPtr address, byte[] buffer, int offset)
        {
            var converter = process.BitConverter;

            switch (type)
            {
                case "int8":
                    return unchecked((sbyte)buffer[offset]);

                case "uint8":
                    return buffer[offset];

                case "bool":
                    return buffer[offset] != 0;

                case "int16":
                    return converter.ToInt16(buffer, offset);

                case "uint16":
                    return converter.ToUInt16(buffer, offset);

                case "int32":
                    return converter.ToInt32(buffer, offset);

                case "uint32":
                    return converter.ToUInt32(buffer, offset);

                case "int64":
                    return converter.ToInt64(buffer, offset);

                case "uint64":
                    return converter.ToUInt64(buffer, offset);

                case "float":
                    return converter.ToSingle(buffer, offset);

                case "double":
                    return converter.ToDouble(buffer, offset);

                case "pointer":
                {
                    var value = IntPtr.Size == 8
                        ? new IntPtr(converter.ToInt64(buffer, offset))
                        : new IntPtr(converter.ToInt32(buffer, offset));

                    return new JObject
                    {
                        ["value"] = Format.Hex(value),
                        ["module"] = process.GetModuleToPointer(value)?.Name,
                        ["section"] = process.GetSectionToPointer(value)?.Name
                    };
                }

                default:
                    throw new InvalidArgumentsException($"'type' must be one of {string.Join(", ", ValueTypes)}, not '{type}'");
            }
        }

        private static byte[] Encode(RemoteProcess process, string type, string literal)
        {
            var converter = process.BitConverter;
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            try
            {
                switch (type)
                {
                    case "int8":
                        return new[] { unchecked((byte)sbyte.Parse(literal, culture)) };

                    case "uint8":
                        return new[] { byte.Parse(literal, culture) };

                    case "bool":
                        return new[] { (byte)(bool.Parse(literal) ? 1 : 0) };

                    case "int16":
                        return converter.GetBytes(short.Parse(literal, culture));

                    case "uint16":
                        return converter.GetBytes(ushort.Parse(literal, culture));

                    case "int32":
                        return converter.GetBytes(int.Parse(literal, culture));

                    case "uint32":
                        return converter.GetBytes(uint.Parse(literal, culture));

                    case "int64":
                        return converter.GetBytes(long.Parse(literal, culture));

                    case "uint64":
                        return converter.GetBytes(ulong.Parse(literal, culture));

                    case "float":
                        return converter.GetBytes(float.Parse(literal, culture));

                    case "double":
                        return converter.GetBytes(double.Parse(literal, culture));

                    case "pointer":
                    {
                        var address = ToolArguments.ParseAddress("value", literal);

                        return IntPtr.Size == 8
                            ? converter.GetBytes(address.ToInt64())
                            : converter.GetBytes(unchecked((int)address.ToInt64()));
                    }

                    default:
                        throw new InvalidArgumentsException($"'type' must be one of {string.Join(", ", ValueTypes)}, not '{type}'");
                }
            }
            catch (FormatException)
            {
                throw new InvalidArgumentsException($"'{literal}' is not a valid {type} value");
            }
            catch (OverflowException)
            {
                throw new InvalidArgumentsException($"'{literal}' does not fit into a {type}");
            }
        }
    }
}
