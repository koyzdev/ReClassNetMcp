using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Tools
{
    internal sealed class ToolRegistry
    {
        private readonly List<ToolDefinition> ordered = new List<ToolDefinition>();

        private readonly Dictionary<string, ToolDefinition> byName = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        public void Add(ToolDefinition tool)
        {
            if (byName.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'");
            }

            byName.Add(tool.Name, tool);
            ordered.Add(tool);
        }

        public bool TryGet(string name, out ToolDefinition tool)
        {
            return byName.TryGetValue(name, out tool);
        }

        public JArray Describe(bool includeMutations)
        {
            var array = new JArray();

            foreach (var tool in ordered)
            {
                if (tool.RequiresMutations && !includeMutations)
                {
                    continue;
                }

                array.Add(tool.Describe());
            }

            return array;
        }
    }
}
