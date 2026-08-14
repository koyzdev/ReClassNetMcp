using System;
using System.IO;
using System.Text;

namespace ReClassNetMcp.Configuration
{
    //
    // Both the settings file and the instance files are read by other processes while we
    // are writing them, so we never write in place. Content goes to a sibling temporary
    // and is swapped in as one step, which leaves a reader with either the old file or
    // the new one and never a truncated one. File.Replace needs the target to exist
    // already, hence the Move on first write. No BOM: these are read back by JSON
    // parsers, and plenty of them treat a leading BOM as a syntax error.
    //
    internal static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        public static void Write(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, Utf8WithoutBom);

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }
}
