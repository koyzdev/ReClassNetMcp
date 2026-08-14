using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner;
using ReClassNET.MemoryScanner.Comparer;
using ReClassNetMcp.Model;

namespace ReClassNetMcp.Tools
{
    internal sealed class ScannerTools
    {
        private const int MaximumAlignment = 64;

        private const int MaximumTextLength = 4096;

        private readonly ToolContext context;

        //
        // One scanner is shared by every request thread, so gate covers all of it: the Scanner,
        // its settings, the value type its stores were written with, and the token source of a
        // running search. pending doubles as the "a scan is in flight" flag, because the host
        // has no way to ask a Scanner whether it is busy. A Scanner cannot be rewound to its
        // first scan either, which is why scan_reset disposes it and builds a new one.
        //
        private readonly object gate = new object();

        private readonly ProgressSink progress;

        private Scanner scanner;

        private ScanSettings settings;

        private ScanValueType valueType;

        private CancellationTokenSource source;

        private Task<bool> pending;

        private volatile int lastProgress;

        public ScannerTools(ToolContext context)
        {
            this.context = context;

            progress = new ProgressSink(this);
        }

        public void Register(ToolRegistry registry)
        {
            registry.Add(new ToolDefinition(
                "scan_start",
                "Start memory scan",
                "Start a fresh memory scan of the attached process and block until it finishes. Any previous scan and its undo history are disposed first, so page through scan_results before starting over. 'valueType' selects the comparer and the storage format of the results and cannot be changed afterwards; scan_next inherits it. 'compareType' defaults to Equal, and the previous-value modes (Changed, NotChanged, Increased, IncreasedOrEqual, Decreased, DecreasedOrEqual) belong to scan_next because they need a recorded previous value. Unknown records every position the stride visits, which is tens of millions of results, and only makes sense as the first step of a change hunt. Pass alignment 1 for byte, arrayOfBytes, string and regex scans, because the default stride of 4 steps over most of their matches. Requires an attached process, so call attach_process first, then read the matches with scan_results.",
                Schema.Object(
                    Schema.Required("valueType", Schema.Enum("byte", "short", "integer", "long", "float", "double", "arrayOfBytes", "string", "regex"), "What to interpret memory as. byte/short/integer/long are two's complement integers, float is 4 bytes, double is 8 bytes, arrayOfBytes and regex use 'pattern', string uses 'value'"),
                    Schema.Optional("compareType", Schema.Enum("Equal", "NotEqual", "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual", "Between", "BetweenOrEqual", "Unknown"), "How to compare, default Equal. Between and BetweenOrEqual need 'value2'. arrayOfBytes, string and regex support Equal only"),
                    Schema.Optional("value", Schema.Text(), "The value to search for, always as a string: \"1234\" or \"0x4d2\" for integers, \"100.5\" for float and double, the text itself for a string scan. Not needed for Unknown"),
                    Schema.Optional("value2", Schema.Text(), "Upper bound for Between and BetweenOrEqual, same encoding as 'value'. Swapped automatically when it is smaller than 'value'"),
                    Schema.Optional("pattern", Schema.Text(), "For arrayOfBytes a hex byte pattern with optional nibble wildcards, e.g. \"AA BB ?? DD\". For regex a .NET regular expression matched against the decoded text"),
                    Schema.Optional("encoding", Schema.Enum("utf8", "utf16", "utf32"), "Text encoding for string and regex scans, default utf8. utf16 is little endian UTF-16, the usual Windows wide string"),
                    Schema.Optional("caseSensitive", Schema.Bool(), "Case sensitivity of string and regex scans, default true"),
                    Schema.Optional("roundMode", Schema.Enum("Strict", "Normal", "Truncate"), "Float and double matching, default Normal. Strict compares the value rounded to 'significantDigits', Normal accepts anything within one unit of the last significant digit, Truncate compares the integer parts only"),
                    Schema.Optional("significantDigits", Schema.Integer(0, 15), "Fraction digits float and double comparisons round to. Derived from the digits after the decimal point in 'value' when omitted"),
                    Schema.Optional("startAddress", Schema.Address(), "Lowest address to scan, hexadecimal, default 0"),
                    Schema.Optional("stopAddress", Schema.Address(), "Highest address to scan, hexadecimal, default the top of the host address space"),
                    Schema.Optional("alignment", Schema.Integer(1, MaximumAlignment), "Byte stride the scanner walks with, default 4. Pass 1 to scan unaligned, which is what byte, arrayOfBytes, string and regex scans need, otherwise most matches are stepped over. ScanSettings.EnableFastScan is dead code in the host, the stride is always applied"),
                    Schema.Optional("scanWritable", Schema.Enum("yes", "no", "any"), "Require, exclude or ignore writable sections, default yes"),
                    Schema.Optional("scanExecutable", Schema.Enum("yes", "no", "any"), "Require, exclude or ignore executable sections, default any"),
                    Schema.Optional("scanCopyOnWrite", Schema.Enum("yes", "no", "any"), "Require, exclude or ignore copy on write sections, default no"),
                    Schema.Optional("scanPrivate", Schema.Bool(), "Include private memory, default true"),
                    Schema.Optional("scanImage", Schema.Bool(), "Include mapped image memory, default true"),
                    Schema.Optional("scanMapped", Schema.Bool(), "Include mapped file memory, default false")),
                Schema.Object(
                    Schema.Required("completed", Schema.Bool(), "True when the scan ran to the end, false when it was cancelled"),
                    Schema.Required("totalResultCount", Schema.Integer(), "Number of matches the scanner now holds"),
                    Schema.Required("valueType", Schema.Text(), "Value type the results are stored as"),
                    Schema.Required("durationMs", Schema.Integer(), "Wall clock duration of the scan in milliseconds")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => ScanStart(arguments, token)));

            registry.Add(new ToolDefinition(
                "scan_next",
                "Refine memory scan",
                "Refine the current scan by re-reading only the addresses that already matched, which is how a value is narrowed down between two observations. Requires scan_start to have run, otherwise it fails. The value type is inherited from the running scan and cannot be changed, because the stored results are typed. This is where Changed, NotChanged, Increased, IncreasedOrEqual, Decreased, DecreasedOrEqual work, since they compare against the value recorded by the previous scan. Use scan_undo to step back one refinement and scan_results to read the matches.",
                Schema.Object(
                    Schema.Optional("compareType", Schema.Enum("Equal", "NotEqual", "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual", "Between", "BetweenOrEqual", "Changed", "NotChanged", "Increased", "IncreasedOrEqual", "Decreased", "DecreasedOrEqual"), "How to compare, default Equal. Changed, NotChanged, Increased, IncreasedOrEqual, Decreased and DecreasedOrEqual compare against the previous value and need no 'value'"),
                    Schema.Optional("value", Schema.Text(), "The value to search for, same encoding as scan_start. Not needed for the previous-value compare types"),
                    Schema.Optional("value2", Schema.Text(), "Upper bound for Between and BetweenOrEqual"),
                    Schema.Optional("pattern", Schema.Text(), "Byte pattern or regular expression when the running scan is an arrayOfBytes or regex scan"),
                    Schema.Optional("encoding", Schema.Enum("utf8", "utf16", "utf32"), "Text encoding for string and regex scans, default utf8"),
                    Schema.Optional("caseSensitive", Schema.Bool(), "Case sensitivity of string and regex scans, default true"),
                    Schema.Optional("roundMode", Schema.Enum("Strict", "Normal", "Truncate"), "Float and double matching, default Normal"),
                    Schema.Optional("significantDigits", Schema.Integer(0, 15), "Fraction digits float and double comparisons round to")),
                Schema.Object(
                    Schema.Required("completed", Schema.Bool(), "True when the scan ran to the end, false when it was cancelled"),
                    Schema.Required("totalResultCount", Schema.Integer(), "Number of matches that survived the refinement"),
                    Schema.Required("valueType", Schema.Text(), "Value type the results are stored as"),
                    Schema.Required("durationMs", Schema.Integer(), "Wall clock duration of the scan in milliseconds")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => ScanNext(arguments, token)));

            registry.Add(new ToolDefinition(
                "scan_results",
                "Read scan results",
                "Page through the matches of the last scan. Every item is {address, valueType, value, size} with an absolute address; arrayOfBytes matches carry the bytes as hex in 'value' plus a 'base64' sibling. Narrow a large result set with scan_next before paging through it, and record the interesting addresses with create_class and the node tools.",
                Schema.Object(
                    Schema.Optional("offset", Schema.Integer(0, int.MaxValue), "Matches to skip, default 0"),
                    Schema.Optional("limit", Schema.Integer(0, 1000), "Matches to return, default 100")),
                Schema.Object(
                    Schema.Required("items", Schema.ArrayOf(Schema.AnyObject()), "Matches as {address, valueType, value, size}"),
                    Schema.Required("offset", Schema.Integer(), "Applied offset"),
                    Schema.Required("limit", Schema.Integer(), "Applied limit"),
                    Schema.Required("count", Schema.Integer(), "Returned item count"),
                    Schema.Required("total", Schema.Integer(), "Total match count of the last scan"),
                    Schema.Required("hasMore", Schema.Bool(), "True when more matches follow")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ScanResults(arguments, token)));

            registry.Add(new ToolDefinition(
                "scan_undo",
                "Undo last scan step",
                "Restore the results of the scan before the last one and discard the newest results for good. The host keeps a ring of three result stores, so at most two undo steps are ever available; call scan_status to see whether one is left.",
                Schema.Object(),
                Schema.Object(
                    Schema.Required("totalResultCount", Schema.Integer(), "Number of matches after the undo")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => ScanUndo()));

            registry.Add(new ToolDefinition(
                "scan_reset",
                "Reset scanner",
                "Throw the scanner away together with every stored result, the undo history and the temporary spill files. The host cannot rewind a scanner to its first scan, so this is the only way to search for a different value type or a different memory region after scan_start. A scan that is still running is cancelled first.",
                Schema.Object(),
                Schema.Object(
                    Schema.Required("reset", Schema.Bool(), "Always true")),
                ToolAnnotations.Mutate(),
                false,
                (arguments, token) => ScanReset(token)));

            registry.Add(new ToolDefinition(
                "scan_status",
                "Scanner status",
                "Report whether a scanner exists, the value type its results are stored as, how many matches it holds, whether scan_undo would succeed, and the progress percentage last reported by a running scan. Returns active=false when no scan has been started.",
                Schema.Object(),
                Schema.Object(
                    Schema.Required("active", Schema.Bool(), "True when a scanner exists"),
                    Schema.Required("valueType", Schema.Text(), "Value type of the stored results, null when inactive"),
                    Schema.Required("totalResultCount", Schema.Integer(), "Number of matches currently held"),
                    Schema.Required("canUndo", Schema.Bool(), "True when scan_undo would restore an older result set"),
                    Schema.Required("lastProgress", Schema.Integer(), "Progress percentage last reported by a scan")),
                ToolAnnotations.Read(),
                false,
                (arguments, token) => ScanStatus()));
        }

        private ToolResult ScanStart(ToolArguments arguments, CancellationToken token)
        {
            var process = context.RequireProcess();

            var type = ParseEnum<ScanValueType>("valueType", arguments.String("valueType"));
            var compareType = ParseEnum<ScanCompareType>("compareType", arguments.OptionalString("compareType", "Equal"));

            if (UsesPreviousValue(compareType))
            {
                throw new InvalidArgumentsException($"'{compareType}' compares against the value recorded by an earlier scan, so it only works with scan_next");
            }

            var comparer = BuildComparer(process, type, compareType, arguments);
            var scanSettings = BuildSettings(arguments, type);

            if (!process.Sections.Any())
            {
                process.UpdateProcessInformations();
            }

            Scanner active;
            Task<bool> task;
            CancellationTokenSource linked;
            var watch = new Stopwatch();

            lock (gate)
            {
                if (pending != null)
                {
                    throw new ToolException("A scan is already running", "Wait for it to finish, or call scan_reset to cancel it and drop its results.");
                }

                scanner?.Dispose();
                source?.Dispose();

                //
                // The value type is kept next to the settings because it is baked into both the
                // comparer and the layout of the result stores. Scanning with one type and
                // reading back with another deserialises the spilled blocks wrongly, so
                // scan_next reuses this and never takes a type of its own.
                //
                settings = scanSettings;
                valueType = type;
                lastProgress = 0;

                active = new Scanner(process, scanSettings);
                scanner = active;
                source = new CancellationTokenSource();

                linked = CancellationTokenSource.CreateLinkedTokenSource(token, source.Token);

                watch.Start();
                task = active.Search(comparer, progress, linked.Token);
                pending = task;
            }

            return AwaitScan(active, task, linked, watch, type);
        }

        private ToolResult ScanNext(ToolArguments arguments, CancellationToken token)
        {
            var process = context.RequireProcess();

            var compareType = ParseEnum<ScanCompareType>("compareType", arguments.OptionalString("compareType", "Equal"));

            if (compareType == ScanCompareType.Unknown)
            {
                throw new InvalidArgumentsException("'Unknown' records every position instead of comparing and is only valid as the first scan, so pass it to scan_start");
            }

            ScanValueType type;

            lock (gate)
            {
                if (scanner == null || settings == null)
                {
                    throw new ToolException("No scan is active", "Call scan_start first; scan_next only refines the results of an existing scan.");
                }

                type = settings.ValueType;
            }

            var comparer = BuildComparer(process, type, compareType, arguments);

            Scanner active;
            Task<bool> task;
            CancellationTokenSource linked;
            var watch = new Stopwatch();

            lock (gate)
            {
                if (pending != null)
                {
                    throw new ToolException("A scan is already running", "Wait for it to finish, or call scan_reset to cancel it and drop its results.");
                }

                //
                // The comparer was built outside the lock, because parsing its arguments can
                // throw. This second check is not redundant: another request may have run
                // scan_reset or scan_start in between, and the comparer in hand would then be
                // typed for a scanner that no longer exists.
                //
                if (scanner == null || settings == null || settings.ValueType != type)
                {
                    throw new ToolException("The active scan changed while the comparer was built", "Call scan_status and then scan_start or scan_next again.");
                }

                lastProgress = 0;

                active = scanner;
                source?.Dispose();
                source = new CancellationTokenSource();

                linked = CancellationTokenSource.CreateLinkedTokenSource(token, source.Token);

                watch.Start();
                task = active.Search(comparer, progress, linked.Token);
                pending = task;
            }

            return AwaitScan(active, task, linked, watch, type);
        }

        private ToolResult AwaitScan(Scanner active, Task<bool> task, CancellationTokenSource linked, Stopwatch watch, ScanValueType type)
        {
            var completed = false;

            //
            // Blocking the request thread on the search is deliberate: an MCP call has to carry
            // its own result and there is nobody to hand a task to. A cancelled search becomes
            // completed=false rather than an error, because its partial results are in the
            // store and still worth paging through.
            //
            try
            {
                completed = task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                watch.Stop();
                linked.Dispose();

                lock (gate)
                {
                    pending = null;
                }
            }

            int total;

            lock (gate)
            {
                total = active.TotalResultCount;
            }

            var structured = new JObject
            {
                ["completed"] = completed,
                ["totalResultCount"] = total,
                ["valueType"] = ValueTypeName(type),
                ["durationMs"] = watch.ElapsedMilliseconds
            };

            return ToolResult.Ok(structured);
        }

        private ToolResult ScanResults(ToolArguments arguments, CancellationToken token)
        {
            var offset = arguments.Count("offset", 0, int.MaxValue);
            var limit = arguments.Count("limit", 100, 1000);

            var items = new JArray();
            int total;

            lock (gate)
            {
                if (scanner == null)
                {
                    throw new ToolException("No scan results are available", "Call scan_start first.");
                }

                total = scanner.TotalResultCount;

                var index = 0;
                foreach (var result in scanner.GetResults())
                {
                    token.ThrowIfCancellationRequested();

                    if (index >= offset)
                    {
                        if (items.Count >= limit)
                        {
                            break;
                        }

                        items.Add(DescribeResult(result));
                    }

                    ++index;
                }
            }

            return ToolResult.Ok(Format.Page(items, offset, limit, total));
        }

        private ToolResult ScanUndo()
        {
            int total;

            lock (gate)
            {
                if (scanner == null)
                {
                    throw new ToolException("No scan is active", "Call scan_start first.");
                }

                if (pending != null)
                {
                    throw new ToolException("A scan is running", "Wait for it to finish before undoing.");
                }

                if (!scanner.CanUndoLastScan)
                {
                    throw new ToolException("There is nothing left to undo", "The host keeps a ring of three result stores, so only two undo steps exist and the current results are the oldest one kept.");
                }

                scanner.UndoLastScan();

                total = scanner.TotalResultCount;
            }

            return ToolResult.Ok(new JObject { ["totalResultCount"] = total });
        }

        private ToolResult ScanReset(CancellationToken token)
        {
            CancelRunningScan(token);

            lock (gate)
            {
                scanner?.Dispose();
                scanner = null;

                source?.Dispose();
                source = null;

                settings = null;
                lastProgress = 0;
            }

            return ToolResult.Ok(new JObject { ["reset"] = true });
        }

        private ToolResult ScanStatus()
        {
            JObject structured;

            lock (gate)
            {
                var active = scanner != null;

                structured = new JObject
                {
                    ["active"] = active,
                    ["valueType"] = active ? ValueTypeName(valueType) : null,
                    ["totalResultCount"] = active ? scanner.TotalResultCount : 0,
                    ["canUndo"] = active && scanner.CanUndoLastScan,
                    ["lastProgress"] = lastProgress
                };
            }

            return ToolResult.Ok(structured);
        }

        private void CancelRunningScan(CancellationToken token)
        {
            Task<bool> task;
            CancellationTokenSource running;

            lock (gate)
            {
                task = pending;
                running = source;
            }

            if (task == null)
            {
                return;
            }

            running?.Cancel();

            try
            {
                task.Wait(token);
            }
            catch (AggregateException)
            {
            }

            //
            // Cancelling the token only ends the search. pending is cleared by whichever thread
            // sits in AwaitScan, and waiting for that here is what makes scan_reset safe to call
            // while a scan runs: the Scanner must not be disposed under its own worker.
            //
            while (true)
            {
                lock (gate)
                {
                    if (pending == null)
                    {
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }

        private ScanSettings BuildSettings(ToolArguments arguments, ScanValueType type)
        {
            var scanSettings = new ScanSettings
            {
                ValueType = type
            };

            if (arguments.Has("startAddress"))
            {
                scanSettings.StartAddress = arguments.Address("startAddress");
            }

            if (arguments.Has("stopAddress"))
            {
                scanSettings.StopAddress = arguments.Address("stopAddress");
            }

            if (Unsigned(scanSettings.StopAddress) <= Unsigned(scanSettings.StartAddress))
            {
                throw new InvalidArgumentsException($"'stopAddress' {Format.Hex(scanSettings.StopAddress)} must be greater than 'startAddress' {Format.Hex(scanSettings.StartAddress)}");
            }

            var alignment = arguments.Count("alignment", 4, MaximumAlignment);
            if (alignment < 1)
            {
                throw new InvalidArgumentsException("'alignment' must be at least 1; a stride of 0 would make the scanner spin forever");
            }

            //
            // The stride is always applied. ScanSettings.EnableFastScan is dead in the host and
            // the worker steps by FastScanAlignment either way, so a byte or string scan that
            // keeps the default of 4 walks over three quarters of its own matches.
            //
            scanSettings.FastScanAlignment = alignment;

            scanSettings.ScanWritableMemory = ParseState(arguments, "scanWritable", SettingState.Yes);
            scanSettings.ScanExecutableMemory = ParseState(arguments, "scanExecutable", SettingState.Indeterminate);
            scanSettings.ScanCopyOnWriteMemory = ParseState(arguments, "scanCopyOnWrite", SettingState.No);
            scanSettings.ScanPrivateMemory = arguments.Bool("scanPrivate", true);
            scanSettings.ScanImageMemory = arguments.Bool("scanImage", true);
            scanSettings.ScanMappedMemory = arguments.Bool("scanMapped", false);

            return scanSettings;
        }

        private static IScanComparer BuildComparer(RemoteProcess process, ScanValueType type, ScanCompareType compareType, ToolArguments arguments)
        {
            switch (type)
            {
                case ScanValueType.Byte:
                case ScanValueType.Short:
                case ScanValueType.Integer:
                case ScanValueType.Long:
                    return BuildIntegralComparer(process, type, compareType, arguments);
                case ScanValueType.Float:
                case ScanValueType.Double:
                    return BuildFloatingComparer(process, type, compareType, arguments);
                case ScanValueType.ArrayOfBytes:
                    RequireEqual(type, compareType);

                    return BuildArrayOfBytesComparer(arguments);
                case ScanValueType.String:
                    RequireEqual(type, compareType);

                    return BuildStringComparer(arguments);
                default:
                    RequireEqual(type, compareType);

                    return BuildRegexComparer(arguments);
            }
        }

        private static IScanComparer BuildIntegralComparer(RemoteProcess process, ScanValueType type, ScanCompareType compareType, ToolArguments arguments)
        {
            ReadIntegralRange(arguments, type, compareType, out var value1, out var value2);

            switch (type)
            {
                case ScanValueType.Byte:
                    return new ByteMemoryComparer(compareType, unchecked((byte)value1), unchecked((byte)value2));
                case ScanValueType.Short:
                    return new ShortMemoryComparer(compareType, unchecked((short)value1), unchecked((short)value2), process.BitConverter);
                case ScanValueType.Integer:
                    return new IntegerMemoryComparer(compareType, unchecked((int)value1), unchecked((int)value2), process.BitConverter);
                default:
                    return new LongMemoryComparer(compareType, value1, value2, process.BitConverter);
            }
        }

        private static IScanComparer BuildFloatingComparer(RemoteProcess process, ScanValueType type, ScanCompareType compareType, ToolArguments arguments)
        {
            var roundMode = ParseEnum<ScanRoundMode>("roundMode", arguments.OptionalString("roundMode", "Normal"));

            var text1 = "0";
            var text2 = "0";

            if (UsesValue(compareType))
            {
                text1 = arguments.String("value").Trim();

                if (NeedsRange(compareType))
                {
                    if (!arguments.Has("value2"))
                    {
                        throw new InvalidArgumentsException($"'{compareType}' needs both 'value' and 'value2' to define the range");
                    }

                    text2 = arguments.String("value2").Trim();
                }
            }

            var value1 = ParseFloating("value", text1);
            var value2 = ParseFloating("value2", text2);

            if (NeedsRange(compareType) && value1 > value2)
            {
                var swap = value1;
                value1 = value2;
                value2 = swap;
            }

            var digits = arguments.Has("significantDigits")
                ? arguments.Count("significantDigits", 0, 15)
                : Math.Max(FractionDigits(text1), FractionDigits(text2));

            if (type == ScanValueType.Float)
            {
                return new FloatMemoryComparer(compareType, roundMode, digits, (float)value1, (float)value2, process.BitConverter);
            }

            return new DoubleMemoryComparer(compareType, roundMode, digits, value1, value2, process.BitConverter);
        }

        private static IScanComparer BuildArrayOfBytesComparer(ToolArguments arguments)
        {
            var text = arguments.String("pattern").Trim();

            if (text.Length == 0)
            {
                throw new InvalidArgumentsException("'pattern' must not be empty for an arrayOfBytes scan");
            }

            if (text.Length > MaximumTextLength)
            {
                throw new InvalidArgumentsException($"'pattern' must not exceed {MaximumTextLength} characters");
            }

            BytePattern pattern;

            try
            {
                pattern = BytePattern.Parse(text);
            }
            catch (ArgumentException)
            {
                throw new InvalidArgumentsException($"'pattern' is not a valid byte pattern: {text}. Use hex bytes with optional nibble wildcards, e.g. \"AA BB ?? DD\"");
            }

            if (pattern.Length == 0)
            {
                throw new InvalidArgumentsException($"'pattern' does not contain a single byte: {text}");
            }

            return new ArrayOfBytesMemoryComparer(pattern);
        }

        private static IScanComparer BuildStringComparer(ToolArguments arguments)
        {
            var text = arguments.String("value");

            if (text.Length == 0)
            {
                throw new InvalidArgumentsException("'value' must not be empty for a string scan");
            }

            if (text.Length > MaximumTextLength)
            {
                throw new InvalidArgumentsException($"'value' must not exceed {MaximumTextLength} characters");
            }

            return new StringMemoryComparer(text, ParseEncoding(arguments), arguments.Bool("caseSensitive", true));
        }

        private static IScanComparer BuildRegexComparer(ToolArguments arguments)
        {
            var text = arguments.String("pattern");

            if (text.Length == 0)
            {
                throw new InvalidArgumentsException("'pattern' must not be empty for a regex scan");
            }

            if (text.Length > MaximumTextLength)
            {
                throw new InvalidArgumentsException($"'pattern' must not exceed {MaximumTextLength} characters");
            }

            try
            {
                return new RegexStringMemoryComparer(text, ParseEncoding(arguments), arguments.Bool("caseSensitive", true));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidArgumentsException($"'pattern' is not a valid .NET regular expression: {ex.Message}");
            }
        }

        private static void ReadIntegralRange(ToolArguments arguments, ScanValueType type, ScanCompareType compareType, out long value1, out long value2)
        {
            value1 = 0;
            value2 = 0;

            if (!UsesValue(compareType))
            {
                return;
            }

            value1 = ParseIntegral("value", arguments.String("value"), type);

            if (!NeedsRange(compareType))
            {
                return;
            }

            if (!arguments.Has("value2"))
            {
                throw new InvalidArgumentsException($"'{compareType}' needs both 'value' and 'value2' to define the range");
            }

            value2 = ParseIntegral("value2", arguments.String("value2"), type);

            if (value1 > value2)
            {
                var swap = value1;
                value1 = value2;
                value2 = swap;
            }
        }

        private static long ParseIntegral(string name, string text, ScanValueType type)
        {
            var trimmed = text.Trim();

            if (trimmed.Length == 0)
            {
                throw new InvalidArgumentsException($"'{name}' must not be empty");
            }

            long value;

            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var digits = trimmed.Substring(2);

                if (digits.Length == 0 || digits.Length > 16 || !ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    throw new InvalidArgumentsException($"'{name}' is not a valid hexadecimal number: {text}");
                }

                value = unchecked((long)hex);
            }
            else if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                value = signed;
            }
            else if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
            {
                value = unchecked((long)unsigned);
            }
            else
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid integer: {text}. Use decimal digits or a 0x prefixed hexadecimal number");
            }

            switch (type)
            {
                case ScanValueType.Byte:
                    RequireRange(name, text, value, byte.MinValue, byte.MaxValue);
                    break;
                case ScanValueType.Short:
                    RequireRange(name, text, value, short.MinValue, ushort.MaxValue);
                    break;
                case ScanValueType.Integer:
                    RequireRange(name, text, value, int.MinValue, uint.MaxValue);
                    break;
            }

            return value;
        }

        private static void RequireRange(string name, string text, long value, long minimum, long maximum)
        {
            if (value < minimum || value > maximum)
            {
                throw new InvalidArgumentsException($"'{name}' must be between {minimum} and {maximum} for this value type, got {text}");
            }
        }

        private static double ParseFloating(string name, string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidArgumentsException($"'{name}' is not a valid decimal number: {text}. Use a dot as the decimal separator, e.g. \"100.5\"");
            }

            return value;
        }

        private static int FractionDigits(string text)
        {
            var index = text.IndexOf('.');

            if (index < 0)
            {
                return 0;
            }

            return text.Length - 1 - index;
        }

        private static Encoding ParseEncoding(ToolArguments arguments)
        {
            var text = arguments.OptionalString("encoding", "utf8");

            if (string.Equals(text, "utf8", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8;
            }

            if (string.Equals(text, "utf16", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.Unicode;
            }

            if (string.Equals(text, "utf32", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF32;
            }

            throw new InvalidArgumentsException($"'encoding' must be one of utf8, utf16, utf32, got '{text}'");
        }

        private static SettingState ParseState(ToolArguments arguments, string name, SettingState fallback)
        {
            var text = arguments.OptionalString(name, null);

            if (text == null)
            {
                return fallback;
            }

            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return SettingState.Yes;
            }

            if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
            {
                return SettingState.No;
            }

            if (string.Equals(text, "any", StringComparison.OrdinalIgnoreCase))
            {
                return SettingState.Indeterminate;
            }

            throw new InvalidArgumentsException($"'{name}' must be one of yes, no, any, got '{text}'");
        }

        private static void RequireEqual(ScanValueType type, ScanCompareType compareType)
        {
            if (compareType != ScanCompareType.Equal)
            {
                throw new InvalidArgumentsException($"{ValueTypeName(type)} scans only support the 'Equal' compare type, got '{compareType}'");
            }
        }

        private static bool UsesValue(ScanCompareType compareType)
        {
            return compareType != ScanCompareType.Unknown && !UsesPreviousValue(compareType);
        }

        private static bool UsesPreviousValue(ScanCompareType compareType)
        {
            switch (compareType)
            {
                case ScanCompareType.Changed:
                case ScanCompareType.NotChanged:
                case ScanCompareType.Increased:
                case ScanCompareType.IncreasedOrEqual:
                case ScanCompareType.Decreased:
                case ScanCompareType.DecreasedOrEqual:
                    return true;
                default:
                    return false;
            }
        }

        private static bool NeedsRange(ScanCompareType compareType)
        {
            return compareType == ScanCompareType.Between || compareType == ScanCompareType.BetweenOrEqual;
        }

        private static JObject DescribeResult(ScanResult result)
        {
            var item = new JObject
            {
                ["address"] = Format.Hex(result.Address),
                ["valueType"] = ValueTypeName(result.ValueType),
                ["size"] = result.ValueSize
            };

            switch (result)
            {
                case ByteScanResult byteResult:
                    item["value"] = byteResult.Value;
                    break;
                case ShortScanResult shortResult:
                    item["value"] = shortResult.Value;
                    break;
                case IntegerScanResult integerResult:
                    item["value"] = integerResult.Value;
                    break;
                case LongScanResult longResult:
                    item["value"] = longResult.Value;
                    break;
                case FloatScanResult floatResult:
                    item["value"] = floatResult.Value;
                    break;
                case DoubleScanResult doubleResult:
                    item["value"] = doubleResult.Value;
                    break;
                case ArrayOfBytesScanResult bytesResult:
                    item["value"] = Format.HexBytes(bytesResult.Value);
                    item["base64"] = Convert.ToBase64String(bytesResult.Value);
                    break;
                case StringScanResult stringResult:
                    item["value"] = stringResult.Value;
                    break;
            }

            return item;
        }

        private static string ValueTypeName(ScanValueType type)
        {
            switch (type)
            {
                case ScanValueType.Byte:
                    return "byte";
                case ScanValueType.Short:
                    return "short";
                case ScanValueType.Integer:
                    return "integer";
                case ScanValueType.Long:
                    return "long";
                case ScanValueType.Float:
                    return "float";
                case ScanValueType.Double:
                    return "double";
                case ScanValueType.ArrayOfBytes:
                    return "arrayOfBytes";
                case ScanValueType.String:
                    return "string";
                default:
                    return "regex";
            }
        }

        private static T ParseEnum<T>(string name, string value) where T : struct
        {
            foreach (var candidate in Enum.GetValues(typeof(T)))
            {
                if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    return (T)candidate;
                }
            }

            throw new InvalidArgumentsException($"'{name}' must be one of {string.Join(", ", Enum.GetNames(typeof(T)))}, got '{value}'");
        }

        private static ulong Unsigned(IntPtr value)
        {
            return unchecked((ulong)value.ToInt64());
        }

        private sealed class ProgressSink : IProgress<int>
        {
            private readonly ScannerTools owner;

            public ProgressSink(ScannerTools owner)
            {
                this.owner = owner;
            }

            public void Report(int value)
            {
                owner.lastProgress = value;
            }
        }
    }
}
