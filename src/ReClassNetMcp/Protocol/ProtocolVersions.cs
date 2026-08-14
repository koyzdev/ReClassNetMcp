using System;
using System.Collections.Generic;

namespace ReClassNetMcp.Protocol
{
    internal static class ProtocolVersions
    {
        //
        // 2025-11-25 is the target revision because it is the last one with an initialize
        // handshake. 2026-07-28 replaces that with a stateless model and the clients that
        // are actually deployed do not speak it yet, so it stays deferred instead of being
        // half-supported here.
        //
        // structuredContent does not exist below 2025-06-18. A client that lands on one of
        // the two older revisions gets the text mirror only, which is what the second
        // constant gates. The third is the revision the spec says to assume when a request
        // carries no MCP-Protocol-Version header at all.
        //
        public const string Advertised = "2025-11-25";

        public const string StructuredContentMinimum = "2025-06-18";

        public const string AssumedWhenHeaderMissing = "2025-03-26";

        private static readonly string[] supported =
        {
            "2025-11-25",
            "2025-06-18",
            "2025-03-26",
            "2024-11-05"
        };

        public static IReadOnlyList<string> Supported => supported;

        public static bool IsSupported(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            foreach (var candidate in supported)
            {
                if (string.Equals(candidate, version, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static string Negotiate(string requested)
        {
            return IsSupported(requested) ? requested : Advertised;
        }

        public static bool SupportsStructuredContent(string negotiated)
        {
            //
            // Ordinal compare stands in for a version compare because every revision is an
            // ISO date of the same width, so lexical order is chronological order.
            //
            return string.CompareOrdinal(negotiated, StructuredContentMinimum) >= 0;
        }
    }
}
