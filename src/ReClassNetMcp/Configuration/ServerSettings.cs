using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Configuration
{
    internal sealed class ServerSettings
    {
        public const int DefaultPort = 15850;

        public const int PortScanRange = 100;

        public bool Enabled { get; set; } = true;

        public bool AllowMutations { get; set; } = true;

        public int PreferredPort { get; set; } = DefaultPort;

        public string Token { get; set; }

        public static string Directory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReClass.NET",
            "mcp");

        public static string FilePath => Path.Combine(Directory, "server.json");

        public static ServerSettings Load()
        {
            var settings = new ServerSettings();

            try
            {
                if (File.Exists(FilePath))
                {
                    var document = JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8));

                    settings.Enabled = document.Value<bool?>("enabled") ?? settings.Enabled;
                    settings.AllowMutations = document.Value<bool?>("allowMutations") ?? settings.AllowMutations;
                    settings.PreferredPort = document.Value<int?>("port") ?? settings.PreferredPort;
                    settings.Token = document.Value<string>("token");
                }
            }
            catch (Exception)
            {
                settings = new ServerSettings();
            }

            if (settings.PreferredPort < 1024 || settings.PreferredPort > ushort.MaxValue - PortScanRange)
            {
                settings.PreferredPort = DefaultPort;
            }

            //
            // A freshly minted token is flushed straight away instead of being parked in
            // the host's Settings.CustomData. The host only writes its settings once, at
            // process exit, so a crash or a kill would lose the token and every client
            // config already installed against it would stop authenticating.
            //
            if (string.IsNullOrEmpty(settings.Token))
            {
                settings.Token = GenerateToken();
                settings.Save();
            }

            return settings;
        }

        public void Save()
        {
            var document = new JObject
            {
                ["enabled"] = Enabled,
                ["allowMutations"] = AllowMutations,
                ["port"] = PreferredPort,
                ["token"] = Token
            };

            AtomicFile.Write(FilePath, document.ToString(Formatting.Indented));
        }

        //
        // What gets published for discovery is this digest, never the token itself. The
        // instance file is only there so a client can find the right host, and anything
        // that can read it should be able to confirm which token a server expects without
        // learning the token. Eight bytes is plenty to tell a handful of hosts apart.
        //
        public string TokenFingerprint()
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Token));
                var builder = new StringBuilder(16);

                for (var i = 0; i < 8; ++i)
                {
                    builder.Append(digest[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string GenerateToken()
        {
            var raw = new byte[32];

            using (var random = new RNGCryptoServiceProvider())
            {
                random.GetBytes(raw);
            }

            return Convert.ToBase64String(raw)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
