using System;
using ReClassNET.AddressParser;
using ReClassNET.Memory;

namespace ReClassNetMcp.Model
{
    //
    // RemoteProcess.ParseAddress does exactly this and is the obvious thing to call, but
    // it memoises into a plain unlocked dictionary, so two tool calls parsing formulas at
    // once can corrupt it. Parser and Interpreter underneath it are pure, which is why we
    // go straight to them and keep a single Interpreter as a static.
    //
    internal static class AddressResolver
    {
        private static readonly Interpreter Executor = new Interpreter();

        public static IntPtr Resolve(IProcessReader process, string formula)
        {
            var expression = Parser.Parse(formula);

            return Executor.Execute(expression, process);
        }
    }
}
