using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Install
{
    internal enum InstallTarget
    {
        OhMyPiUser,
        OhMyPiProject,
        ClaudeCode,
        Cursor,
        VsCode,
        Codex
    }

    internal sealed class InstallResult
    {
        public InstallTarget Target { get; set; }

        public string Path { get; set; }

        public bool Created { get; set; }

        public bool Updated { get; set; }

        public string Message { get; set; }
    }

    internal sealed class ClientInstaller
    {
        public const string OhMyPiSchemaUrl = "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json";

        private const int RequestTimeoutMilliseconds = 120000;

        private readonly string serverName;

        private readonly string url;

        private readonly string token;

        public ClientInstaller(string serverName, string url, string token)
        {
            if (string.IsNullOrEmpty(serverName))
            {
                throw new ArgumentException("The server name is required.", nameof(serverName));
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("The endpoint url is required; start the MCP server before installing it.", nameof(url));
            }

            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentException("The bearer token is required.", nameof(token));
            }

            this.serverName = serverName;
            this.url = url;
            this.token = token;
        }

        public string DescribeEntry()
        {
            var document = new JObject
            {
                ["$schema"] = OhMyPiSchemaUrl,
                ["mcpServers"] = new JObject
                {
                    [serverName] = BuildEntry()
                }
            };

            return document.ToString(Formatting.Indented);
        }

        public InstallResult Install(InstallTarget target, string projectDirectory)
        {
            var path = ResolvePath(target, projectDirectory);

            if (target == InstallTarget.Codex)
            {
                var toml = TomlConfigWriter.Write(path, serverName, url, token);

                return Describe(target, path, toml.Created, toml.Replaced, toml.Changed);
            }

            var schemaUrl = IsOhMyPi(target) ? OhMyPiSchemaUrl : null;
            var json = JsonConfigWriter.Write(path, ContainerKey(target), serverName, BuildEntry(), schemaUrl);

            return Describe(target, path, json.Created, json.Replaced, json.Changed);
        }

        public bool TryGetInstalledUrl(InstallTarget target, string projectDirectory, out string installedUrl)
        {
            var path = ResolvePath(target, projectDirectory);

            if (target == InstallTarget.Codex)
            {
                return TomlConfigWriter.TryReadUrl(path, serverName, out installedUrl);
            }

            return JsonConfigWriter.TryReadExistingUrl(path, ContainerKey(target), serverName, out installedUrl);
        }

        public static string ResolvePath(InstallTarget target, string projectDirectory)
        {
            switch (target)
            {
                case InstallTarget.OhMyPiUser:
                    return Path.Combine(UserProfile(), ".omp", "agent", "mcp.json");
                case InstallTarget.OhMyPiProject:
                    return Path.Combine(ProjectRoot(target, projectDirectory), ".omp", "mcp.json");
                case InstallTarget.ClaudeCode:
                    return Path.Combine(UserProfile(), ".claude.json");
                case InstallTarget.Cursor:
                    return Path.Combine(UserProfile(), ".cursor", "mcp.json");
                case InstallTarget.VsCode:
                    return Path.Combine(ProjectRoot(target, projectDirectory), ".vscode", "mcp.json");
                case InstallTarget.Codex:
                    return Path.Combine(UserProfile(), ".codex", "config.toml");
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown install target.");
            }
        }

        public static string ContainerKey(InstallTarget target)
        {
            return target == InstallTarget.VsCode ? "servers" : "mcpServers";
        }

        public static bool RequiresProjectDirectory(InstallTarget target)
        {
            return target == InstallTarget.OhMyPiProject || target == InstallTarget.VsCode;
        }

        public static string DisplayName(InstallTarget target)
        {
            switch (target)
            {
                case InstallTarget.OhMyPiUser:
                    return "oh-my-pi (user)";
                case InstallTarget.OhMyPiProject:
                    return "oh-my-pi (project)";
                case InstallTarget.ClaudeCode:
                    return "Claude Code";
                case InstallTarget.Cursor:
                    return "Cursor";
                case InstallTarget.VsCode:
                    return "VS Code";
                case InstallTarget.Codex:
                    return "Codex";
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown install target.");
            }
        }

        private static bool IsOhMyPi(InstallTarget target)
        {
            return target == InstallTarget.OhMyPiUser || target == InstallTarget.OhMyPiProject;
        }

        private static string UserProfile()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrEmpty(profile))
            {
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            }

            if (string.IsNullOrEmpty(profile))
            {
                throw new InvalidOperationException("The user profile directory could not be determined.");
            }

            return profile;
        }

        private static string ProjectRoot(InstallTarget target, string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                throw new ArgumentException($"{DisplayName(target)} is project scoped and needs a project directory.", nameof(projectDirectory));
            }

            return Path.GetFullPath(projectDirectory);
        }

        private JObject BuildEntry()
        {
            return new JObject
            {
                ["type"] = "http",
                ["url"] = url,
                ["headers"] = new JObject
                {
                    ["Authorization"] = "Bearer " + token
                },
                ["timeout"] = RequestTimeoutMilliseconds
            };
        }

        private InstallResult Describe(InstallTarget target, string path, bool created, bool replaced, bool changed)
        {
            string summary;

            if (created)
            {
                summary = $"Created {path} and registered '{serverName}'.";
            }
            else if (!changed)
            {
                summary = $"{path} already contains a current '{serverName}' entry.";
            }
            else if (replaced)
            {
                summary = $"Updated {path} and replaced the existing '{serverName}' entry.";
            }
            else
            {
                summary = $"Updated {path} and added the '{serverName}' entry.";
            }

            return new InstallResult
            {
                Target = target,
                Path = path,
                Created = created,
                Updated = changed && !created,
                Message = summary + " " + FollowUp(target)
            };
        }

        private string FollowUp(InstallTarget target)
        {
            switch (target)
            {
                case InstallTarget.OhMyPiUser:
                case InstallTarget.OhMyPiProject:
                    return $"In oh-my-pi run /mcp reload, then /mcp test {serverName}.";
                case InstallTarget.ClaudeCode:
                    return $"Restart Claude Code, then run /mcp to check {serverName}.";
                case InstallTarget.Cursor:
                    return $"Reload Cursor and enable {serverName} under Settings > MCP.";
                case InstallTarget.VsCode:
                    return $"Reload the VS Code window, then start {serverName} from .vscode/mcp.json.";
                case InstallTarget.Codex:
                    return $"Restart Codex to pick up {serverName}.";
                default:
                    return string.Empty;
            }
        }
    }
}
