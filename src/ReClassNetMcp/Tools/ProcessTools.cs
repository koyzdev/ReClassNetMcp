using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReClassNET.AddressParser;
using ReClassNET.Core;
using ReClassNET.Memory;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class ProcessTools
    {
        private readonly ToolContext context;

        public ProcessTools(ToolContext context)
        {
            this.context = context;
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "list_processes",
                "List processes",
                "Enumerate every process ReClass.NET can see, so you can pick one to attach to. Filter by a case insensitive substring of the name or the full path.",
                Schema.Object(
                    Schema.Optional("filter", Schema.Text(), "Case insensitive substring matched against name and path"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Items to return, default 100")),
                Schema.Object(Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Processes as {id, name, path}"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Matching process count"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more items follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListProcesses(arguments)));

            registry.Add(new ToolDefinition(
                "attach_process",
                "Attach to process",
                "Attach ReClass.NET to a process by name or by numeric process id. This is a precondition for every memory tool. Call list_processes first if you do not know the exact name.",
                Schema.Object(
                    Schema.Optional("name", Schema.Text(), "Process name, exact match preferred, otherwise a unique substring"),
                    Schema.Optional("id", Schema.Integer(), "Numeric process id, takes precedence over name")),
                Schema.Object(Schema.Required("attached", Schema.Bool(), "True on success"),
                    Schema.Required("process", Schema.AnyObject(), "The attached process")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => AttachProcess(arguments)));

            registry.Add(new ToolDefinition(
                "detach_process",
                "Detach from process",
                "Close the handle ReClass.NET holds on the target process. Memory tools stop working until you attach again.",
                Schema.Object(),
                Schema.Object(Schema.Required("detached", Schema.Bool(), "Always true")),
                ToolAnnotations.Mutate(),
                true,
                (arguments, token) => DetachProcess()));

            registry.Add(new ToolDefinition(
                "process_info",
                "Attached process info",
                "Report the currently attached process, its module and section counts, and the host pointer size. Returns attached=false instead of failing when nothing is attached.",
                Schema.Object(),
                Schema.Object(Schema.Required("attached", Schema.Bool(), "Whether a process is attached")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ProcessInfoResult()));

            registry.Add(new ToolDefinition(
                "list_modules",
                "List modules",
                "List the loaded modules of the attached process with their base address, end address and size. Use a module name in an address formula like <module.exe>+0x1f4.",
                Schema.Object(
                    Schema.Optional("filter", Schema.Text(), "Case insensitive substring matched against name and path"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Items to return, default 100")),
                Schema.Object(Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Modules as {name, path, start, end, size}"),
                    Schema.Required("total", Schema.Integer(), "Matching module count")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListModules(arguments)));

            registry.Add(new ToolDefinition(
                "list_sections",
                "List sections",
                "List the memory sections of the attached process, optionally filtered by name, owning module or category. Category is one of Unknown, CODE, DATA, HEAP.",
                Schema.Object(
                    Schema.Optional("filter", Schema.Text(), "Case insensitive substring matched against section name, module name and module path"),
                    Schema.Optional("category", Schema.Enum("Unknown", "CODE", "DATA", "HEAP"), "Restrict to one section category"),
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Items to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(1, 1000), "Items to return, default 100")),
                Schema.Object(Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Sections as {name, start, end, size, category, type, protection, moduleName, modulePath}"),
                    Schema.Required("total", Schema.Integer(), "Matching section count")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ListSections(arguments)));

            registry.Add(new ToolDefinition(
                "resolve_address",
                "Resolve address formulas",
                "Resolve one or more ReClass.NET address formulas against the attached process and report the module, section and named address each one lands in. Syntax: module names are wrapped in angle brackets, every number is hexadecimal (10 means 0x10), [x] dereferences, and + - * / are supported. Example: [<game.exe>+0x1f4]+0x10. A malformed formula fails only its own entry.",
                Schema.Object(
                    Schema.Optional("formula", Schema.Formula(), "A single formula"),
                    Schema.Optional("formulas", Schema.ArrayOf(Schema.Formula(), 256), "A batch of formulas")),
                Schema.Object(Schema.Required("results", Schema.ArrayOf(Schema.AnyObject()), "One entry per formula, either {formula, address, module, section, namedAddress} or {formula, error}")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ResolveAddresses(arguments, token)));

            registry.Add(new ToolDefinition(
                "control_process",
                "Control process",
                "Suspend, resume or terminate the attached process. Terminate kills the target immediately, which destroys the session and every address you have collected; there is no undo.",
                Schema.Object(
                    Schema.Required("action", Schema.Enum("suspend", "resume", "terminate"), "What to do to the target process")),
                Schema.Object(Schema.Required("action", Schema.Text(), "Echo of the requested action"),
                    Schema.Required("applied", Schema.Bool(), "Always true when no error was raised")),
                ToolAnnotations.Destroy(),
                true,
                (arguments, token) => ControlProcess(arguments)));
        }

        private ToolResult ListProcesses(ToolArguments arguments)
        {
            var filter = arguments.OptionalString("filter", null);
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);

            var processes = Enumerate(filter);

            var items = new JArray();
            foreach (var process in processes.Skip(offset).Take(limit))
            {
                items.Add(new JObject
                {
                    ["id"] = process.Id.ToInt64(),
                    ["name"] = process.Name,
                    ["path"] = process.Path
                });
            }

            return ToolResult.Ok(Format.Page(items, offset, limit, processes.Count));
        }

        private ToolResult AttachProcess(ToolArguments arguments)
        {
            var hasId = arguments.Has("id");
            var hasName = arguments.Has("name");

            if (!hasId && !hasName)
            {
                throw new InvalidArgumentsException("Provide either 'id' or 'name'");
            }

            var processes = Enumerate(null);
            ProcessInfo target;

            if (hasId)
            {
                var id = arguments.Integer("id");
                target = processes.FirstOrDefault(process => process.Id.ToInt64() == id);

                if (target == null)
                {
                    throw new ToolException($"No process with id {id}", "Call list_processes to see the running processes.");
                }
            }
            else
            {
                var name = arguments.String("name");

                var exact = processes.Where(process => string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var candidates = exact.Count > 0
                    ? exact
                    : processes.Where(process => process.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (candidates.Count == 0)
                {
                    throw new ToolException($"No process matching '{name}'", "Call list_processes to see the running processes.");
                }

                if (candidates.Count > 1)
                {
                    var names = string.Join(", ", candidates.Take(10).Select(process => $"{process.Name} ({process.Id.ToInt64()})"));
                    throw new ToolException(
                        $"'{name}' matches {candidates.Count} processes: {names}",
                        "Pass the exact name or the numeric 'id'.");
                }

                target = candidates[0];
            }

            //
            // RemoteProcess.Open compares the ProcessInfo by reference, so handing it the same
            // instance twice is a silent no op. Enumerate() builds fresh instances on every
            // call, which is why the list is never cached, and the host is asked afterwards
            // whether the open really produced a handle instead of trusting the call.
            //
            context.Host.AttachToProcess(target);

            var attached = context.Host.GetAttachedProcess();
            if (!attached.IsAttached)
            {
                throw new ToolException(
                    $"ReClass.NET could not open {target.Name} ({target.Id.ToInt64()})",
                    "The target may require elevation or be protected; start ReClass.NET as administrator.");
            }

            context.Host.Log(HostLogLevel.Information, $"mcp: attached to {target.Name} ({target.Id.ToInt64()})");

            return ToolResult.Ok(new JObject
            {
                ["attached"] = true,
                ["process"] = Describe(attached)
            });
        }

        private ToolResult DetachProcess()
        {
            context.Host.DetachProcess();

            return ToolResult.Ok(new JObject { ["detached"] = true });
        }

        private ToolResult ProcessInfoResult()
        {
            var attached = context.Host.GetAttachedProcess();

            var structured = new JObject
            {
                ["attached"] = attached.IsAttached,
                ["platform"] = context.Host.Platform,
                ["pointerSize"] = context.Host.PointerSize
            };

            if (attached.IsAttached)
            {
                structured["process"] = Describe(attached);
            }

            return ToolResult.Ok(structured);
        }

        private ToolResult ListModules(ToolArguments arguments)
        {
            var filter = arguments.OptionalString("filter", null);
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);

            var modules = context.RequireProcess().Modules
                .Where(module => Matches(filter, module.Name, module.Path))
                .OrderBy(module => module.Start.ToInt64())
                .ToList();

            var items = new JArray();
            foreach (var module in modules.Skip(offset).Take(limit))
            {
                items.Add(new JObject
                {
                    ["name"] = module.Name,
                    ["path"] = module.Path,
                    ["start"] = Format.Hex(module.Start),
                    ["end"] = Format.Hex(module.End),
                    ["size"] = module.Size.ToInt64()
                });
            }

            return ToolResult.Ok(Format.Page(items, offset, limit, modules.Count));
        }

        private ToolResult ListSections(ToolArguments arguments)
        {
            var filter = arguments.OptionalString("filter", null);
            var category = arguments.OptionalString("category", null);
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);

            SectionCategory? wanted = null;
            if (category != null)
            {
                if (!Enum.TryParse(category, true, out SectionCategory parsed))
                {
                    throw new InvalidArgumentsException($"'category' must be one of Unknown, CODE, DATA, HEAP, not '{category}'");
                }

                wanted = parsed;
            }

            var sections = context.RequireProcess().Sections
                .Where(section => Matches(filter, section.Name, section.ModuleName, section.ModulePath))
                .Where(section => wanted == null || section.Category == wanted.Value)
                .OrderBy(section => section.Start.ToInt64())
                .ToList();

            var items = new JArray();
            foreach (var section in sections.Skip(offset).Take(limit))
            {
                items.Add(new JObject
                {
                    ["name"] = section.Name,
                    ["start"] = Format.Hex(section.Start),
                    ["end"] = Format.Hex(section.End),
                    ["size"] = section.Size.ToInt64(),
                    ["category"] = section.Category.ToString(),
                    ["type"] = section.Type.ToString(),
                    ["protection"] = section.Protection.ToString(),
                    ["moduleName"] = section.ModuleName,
                    ["modulePath"] = section.ModulePath
                });
            }

            return ToolResult.Ok(Format.Page(items, offset, limit, sections.Count));
        }

        private ToolResult ResolveAddresses(ToolArguments arguments, CancellationToken token)
        {
            var formulas = new List<string>();

            var single = arguments.OptionalString("formula", null);
            if (single != null)
            {
                formulas.Add(single);
            }

            formulas.AddRange(arguments.Strings("formulas"));

            if (formulas.Count == 0)
            {
                throw new InvalidArgumentsException("Provide 'formula' or a non empty 'formulas' array");
            }

            if (formulas.Count > 256)
            {
                throw new InvalidArgumentsException($"'formulas' must not exceed 256 entries, got {formulas.Count}");
            }

            var process = context.RequireProcess();
            var results = new JArray();

            //
            // Every number in a formula is hexadecimal and module names are wrapped in angle
            // brackets, so <game.exe>+10 lands at +0x10 and a bare game.exe+0x10 does not
            // parse at all. A malformed entry gets its own error object, because a batch of
            // 256 formulas should not be lost to one typo.
            //
            foreach (var formula in formulas)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var address = AddressResolver.Resolve(process, formula);
                    var module = process.GetModuleToPointer(address);
                    var section = process.GetSectionToPointer(address);

                    results.Add(new JObject
                    {
                        ["formula"] = formula,
                        ["address"] = Format.Hex(address),
                        ["module"] = module?.Name,
                        ["moduleOffset"] = module == null ? null : new JValue(Format.Hex(address.ToInt64() - module.Start.ToInt64())),
                        ["section"] = section?.Name,
                        ["namedAddress"] = process.GetNamedAddress(address)
                    });
                }
                catch (ParseException ex)
                {
                    results.Add(new JObject
                    {
                        ["formula"] = formula,
                        ["error"] = ex.Message
                    });
                }
            }

            return ToolResult.Ok(new JObject { ["results"] = results });
        }

        private ToolResult ControlProcess(ToolArguments arguments)
        {
            var action = arguments.String("action");

            ControlRemoteProcessAction mapped;
            switch (action.ToLowerInvariant())
            {
                case "suspend":
                    mapped = ControlRemoteProcessAction.Suspend;
                    break;

                case "resume":
                    mapped = ControlRemoteProcessAction.Resume;
                    break;

                case "terminate":
                    mapped = ControlRemoteProcessAction.Terminate;
                    break;

                default:
                    throw new InvalidArgumentsException($"'action' must be suspend, resume or terminate, not '{action}'");
            }

            var process = context.RequireProcess();

            //
            // The record goes into the host log before the call and not after: a terminate
            // never comes back with a live process to report, and the human watching the
            // window should be able to see who killed the target.
            //
            context.Host.Log(HostLogLevel.Warning, $"mcp: control_process {mapped} on {process.UnderlayingProcess.Name}");

            process.ControlRemoteProcess(mapped);

            return ToolResult.Ok(new JObject
            {
                ["action"] = action.ToLowerInvariant(),
                ["applied"] = true
            });
        }

        private List<ProcessInfo> Enumerate(string filter)
        {
            //
            // The process list comes off the core functions and not off RequireProcess, so
            // list_processes and attach_process keep working while nothing is attached yet.
            //
            var functions = context.Host.Process.CoreFunctions;

            return functions.EnumerateProcesses()
                .Where(process => Matches(filter, process.Name, process.Path))
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.Id.ToInt64())
                .ToList();
        }

        private static bool Matches(string filter, params string[] candidates)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }

            foreach (var candidate in candidates)
            {
                if (candidate != null && candidate.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static JObject Describe(AttachedProcessInfo info)
        {
            return new JObject
            {
                ["id"] = info.Id,
                ["name"] = info.Name,
                ["path"] = info.Path,
                ["valid"] = info.IsValid,
                ["moduleCount"] = info.ModuleCount,
                ["sectionCount"] = info.SectionCount
            };
        }
    }
}
