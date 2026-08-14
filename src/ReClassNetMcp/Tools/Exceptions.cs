using System;

namespace ReClassNetMcp.Tools
{
    internal sealed class InvalidArgumentsException : Exception
    {
        public InvalidArgumentsException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ToolException : Exception
    {
        public string Hint { get; }

        public ToolException(string message)
            : base(message)
        {
        }

        public ToolException(string message, string hint)
            : base(message)
        {
            Hint = hint;
        }
    }
}
