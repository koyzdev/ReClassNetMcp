using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Tools
{
    internal sealed class ToolAnnotations
    {
        public bool ReadOnly { get; set; }

        public bool Destructive { get; set; }

        public bool Idempotent { get; set; }

        public JObject ToJson(string title)
        {
            return new JObject
            {
                ["title"] = title,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
                ["openWorldHint"] = false
            };
        }

        public static ToolAnnotations Read()
        {
            return new ToolAnnotations { ReadOnly = true, Destructive = false, Idempotent = true };
        }

        public static ToolAnnotations Mutate()
        {
            return new ToolAnnotations { ReadOnly = false, Destructive = false, Idempotent = false };
        }

        public static ToolAnnotations Destroy()
        {
            return new ToolAnnotations { ReadOnly = false, Destructive = true, Idempotent = false };
        }
    }

    internal sealed class ToolDefinition
    {
        public string Name { get; }

        public string Title { get; }

        public string Description { get; }

        public JObject InputSchema { get; }

        public JObject OutputSchema { get; }

        public ToolAnnotations Annotations { get; }

        public bool RequiresMutations { get; }

        public Func<ToolArguments, CancellationToken, ToolResult> Handler { get; }

        public ToolDefinition(
            string name,
            string title,
            string description,
            JObject inputSchema,
            JObject outputSchema,
            ToolAnnotations annotations,
            bool requiresMutations,
            Func<ToolArguments, CancellationToken, ToolResult> handler)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Tool name must not be empty", nameof(name));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            Name = name;
            Title = title;
            Description = description;
            InputSchema = inputSchema ?? Schema.Object();
            OutputSchema = outputSchema;
            Annotations = annotations ?? ToolAnnotations.Read();
            RequiresMutations = requiresMutations;
            Handler = handler;
        }

        public JObject Describe()
        {
            var descriptor = new JObject
            {
                ["name"] = Name,
                ["title"] = Title,
                ["description"] = Description,
                ["inputSchema"] = InputSchema,
                ["annotations"] = Annotations.ToJson(Title)
            };

            if (OutputSchema != null)
            {
                descriptor["outputSchema"] = OutputSchema;
            }

            return descriptor;
        }
    }
}
