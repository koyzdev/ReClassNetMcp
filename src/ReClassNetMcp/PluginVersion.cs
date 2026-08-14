using System.Reflection;

namespace ReClassNetMcp
{
    internal static class PluginVersion
    {
        public static string Value { get; } = typeof(PluginVersion).Assembly.GetName().Version.ToString(3);
    }
}
