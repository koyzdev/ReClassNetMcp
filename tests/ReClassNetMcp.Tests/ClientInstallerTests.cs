using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Install;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class ClientInstallerTests : IDisposable
    {
        private readonly string directory;

        public ClientInstallerTests()
        {
            directory = Path.Combine(Path.GetTempPath(), "reclass-mcp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }

        private static ClientInstaller Installer()
        {
            return new ClientInstaller("reclass", "http://127.0.0.1:15850/mcp", "token-abc");
        }

        [Fact]
        public void OhMyPiProjectInstallWritesMcpServersWithSchemaAndBearer()
        {
            var result = Installer().Install(InstallTarget.OhMyPiProject, directory);

            Assert.True(result.Created);

            var path = Path.Combine(directory, ".omp", "mcp.json");
            Assert.Equal(path, result.Path);

            var document = JObject.Parse(File.ReadAllText(path));

            Assert.Equal(
                "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json",
                (string)document["$schema"]);

            var entry = document["mcpServers"]["reclass"];

            Assert.Equal("http", (string)entry["type"]);
            Assert.Equal("http://127.0.0.1:15850/mcp", (string)entry["url"]);
            Assert.Equal("Bearer token-abc", (string)entry["headers"]["Authorization"]);
        }

        [Fact]
        public void VsCodeInstallUsesServersKeyAndNoSchema()
        {
            Installer().Install(InstallTarget.VsCode, directory);

            var document = JObject.Parse(File.ReadAllText(Path.Combine(directory, ".vscode", "mcp.json")));

            Assert.Null(document["mcpServers"]);
            Assert.Null(document["$schema"]);
            Assert.Equal("http://127.0.0.1:15850/mcp", (string)document["servers"]["reclass"]["url"]);
        }

        [Fact]
        public void InstallPreservesUnrelatedConfiguration()
        {
            var path = Path.Combine(directory, ".omp", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            File.WriteAllText(path, new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["ida_mcp"] = new JObject { ["type"] = "http", ["url"] = "http://127.0.0.1:13337/mcp" }
                },
                ["disabledServers"] = new JArray("something"),
                ["unrelated"] = new JObject { ["keep"] = true }
            }.ToString());

            Installer().Install(InstallTarget.OhMyPiProject, directory);

            var document = JObject.Parse(File.ReadAllText(path));

            Assert.Equal("http://127.0.0.1:13337/mcp", (string)document["mcpServers"]["ida_mcp"]["url"]);
            Assert.Equal("something", (string)document["disabledServers"][0]);
            Assert.True((bool)document["unrelated"]["keep"]);
            Assert.NotNull(document["mcpServers"]["reclass"]);
        }

        [Fact]
        public void ReinstallOfTheSameEntryReportsNoChange()
        {
            Installer().Install(InstallTarget.OhMyPiProject, directory);

            var second = Installer().Install(InstallTarget.OhMyPiProject, directory);

            Assert.False(second.Created);
            Assert.False(second.Updated);
        }

        [Fact]
        public void PortChangeReplacesOnlyOurEntry()
        {
            Installer().Install(InstallTarget.OhMyPiProject, directory);

            var moved = new ClientInstaller("reclass", "http://127.0.0.1:15851/mcp", "token-abc");
            var result = moved.Install(InstallTarget.OhMyPiProject, directory);

            Assert.True(result.Updated);
            Assert.True(moved.TryGetInstalledUrl(InstallTarget.OhMyPiProject, directory, out var installed));
            Assert.Equal("http://127.0.0.1:15851/mcp", installed);
        }

        [Fact]
        public void MalformedConfigurationIsRefusedInsteadOfOverwritten()
        {
            var path = Path.Combine(directory, ".omp", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{ this is not json");

            Assert.Throws<InvalidOperationException>(() => Installer().Install(InstallTarget.OhMyPiProject, directory));
            Assert.Equal("{ this is not json", File.ReadAllText(path));
        }

        [Fact]
        public void DescribeEntryContainsTheServerNameAndBearerToken()
        {
            var snippet = Installer().DescribeEntry();

            var document = JObject.Parse(snippet);

            Assert.Equal("Bearer token-abc", (string)document["mcpServers"]["reclass"]["headers"]["Authorization"]);
        }
    }
}
