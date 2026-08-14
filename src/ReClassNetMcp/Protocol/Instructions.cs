namespace ReClassNetMcp.Protocol
{
    internal static class Instructions
    {
        public const string Text =
            "This server drives a live ReClass.NET instance: process attachment, remote memory access, " +
            "memory scanning, and the class/node project model used to reverse engineer structures.\n\n" +
            "Conventions:\n" +
            "- Addresses are hexadecimal strings; both \"0x14000f00\" and \"14000f00\" are accepted, and every " +
            "address in a result is returned as a lowercase \"0x\" string.\n" +
            "- Address formulas follow ReClass.NET syntax: module names must be wrapped in angle brackets and " +
            "all numbers are hexadecimal, e.g. \"<game.exe>+0x1f4\" or \"[<game.exe>+0x1f4]+0x10\".\n" +
            "- A class is identified by its uuid. A node inside a class is identified by \"<uuid>:<i>/<j>\", " +
            "the child index path from the class root. Index paths shift when nodes are inserted or removed, so " +
            "use the handles returned by the mutating call rather than remembered ones.\n" +
            "- Byte payloads are returned as both \"hex\" and \"base64\", and are accepted as either.\n" +
            "- List results are paginated with offset/limit/total/hasMore. Oversized results are replaced by a " +
            "preview and the full payload is retrievable with get_output using the id in _meta.\n" +
            "- Tools that fail for an expected reason return isError with a message and a hint; read it and adjust " +
            "rather than retrying the same call.\n\n" +
            "Typical workflow: list_processes, attach_process, resolve_address to turn a formula into an address, " +
            "read_memory to inspect a window of bytes, suggest_types to get dissector guesses, then create_class " +
            "and the node tools to record the layout, and finally generate_code.";
    }
}
