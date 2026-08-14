using System;
using System.Security.Cryptography;
using System.Text;

namespace ReClassNetMcp.Server
{
    internal static class TokenComparer
    {
        public static bool Matches(string expected, string provided)
        {
            if (expected == null || provided == null)
            {
                return false;
            }

            //
            // Hash both operands first, then compare the digests. The compare then
            // always runs over a fixed 32 bytes no matter what arrived on the wire,
            // so the token length does not leak, and the accumulated xor never exits
            // early, so the count of correct leading bytes does not leak either.
            //
            using (var sha = SHA256.Create())
            {
                var left = sha.ComputeHash(Encoding.UTF8.GetBytes(expected));
                var right = sha.ComputeHash(Encoding.UTF8.GetBytes(provided));

                var difference = 0;
                for (var i = 0; i < left.Length; ++i)
                {
                    difference |= left[i] ^ right[i];
                }

                return difference == 0;
            }
        }
    }
}
