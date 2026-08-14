using System;
using System.Windows.Forms;
using ReClassNET;
using ReClassNetMcp.Install;
using ReClassNetMcp.Protocol;

namespace ReClassNetMcp.Ui
{
    internal sealed class McpMenu
    {
        private const string Caption = "MCP Server";

        private readonly MenuStrip menuStrip;

        private readonly Func<bool> isRunning;

        private readonly Func<string> endpointUrl;

        private readonly Func<string> token;

        private readonly Func<string> serverName;

        private readonly Action start;

        private readonly Action stop;

        private readonly Func<bool> getAllowMutations;

        private readonly Action<bool> setAllowMutations;

        private readonly Func<InstallTarget, string, InstallResult> install;

        private readonly Func<string> describeEntry;

        private ToolStripMenuItem rootItem;

        private ToolStripMenuItem enabledItem;

        private ToolStripMenuItem allowMutationsItem;

        private ToolStripMenuItem copyEndpointItem;

        private McpStatusForm statusForm;

        public McpMenu(
            MenuStrip menuStrip,
            Func<bool> isRunning,
            Func<string> endpointUrl,
            Func<string> token,
            Func<string> serverName,
            Action start,
            Action stop,
            Func<bool> getAllowMutations,
            Action<bool> setAllowMutations,
            Func<InstallTarget, string, InstallResult> install,
            Func<string> describeEntry)
        {
            if (menuStrip == null)
            {
                throw new ArgumentNullException(nameof(menuStrip));
            }

            this.menuStrip = menuStrip;
            this.isRunning = isRunning;
            this.endpointUrl = endpointUrl;
            this.token = token;
            this.serverName = serverName;
            this.start = start;
            this.stop = stop;
            this.getAllowMutations = getAllowMutations;
            this.setAllowMutations = setAllowMutations;
            this.install = install;
            this.describeEntry = describeEntry;
        }

        public void Attach()
        {
            if (rootItem != null)
            {
                return;
            }

            enabledItem = new ToolStripMenuItem("Enabled", null, (sender, e) => ToggleEnabled())
            {
                CheckOnClick = false,
                ToolTipText = "Start or stop the local MCP endpoint"
            };

            allowMutationsItem = new ToolStripMenuItem("Allow mutations", null, (sender, e) => ToggleAllowMutations())
            {
                CheckOnClick = false,
                ToolTipText = "Permit tools that write process memory, edit the project or change process state"
            };

            var statusItem = new ToolStripMenuItem("Status…", null, (sender, e) => ShowStatus());

            copyEndpointItem = new ToolStripMenuItem("Copy endpoint", null, (sender, e) => McpStatusForm.CopyToClipboard(endpointUrl(), "endpoint"));

            var copyTokenItem = new ToolStripMenuItem("Copy token", null, (sender, e) => McpStatusForm.CopyToClipboard(token(), "token"));

            var copyConfigItem = new ToolStripMenuItem("Copy config JSON", null, (sender, e) => McpStatusForm.CopyToClipboard(describeEntry(), "configuration"));

            var installItem = new ToolStripMenuItem("Install for");
            installItem.DropDownItems.Add(CreateInstallItem("oh-my-pi (user)", InstallTarget.OhMyPiUser, false));
            installItem.DropDownItems.Add(CreateInstallItem("oh-my-pi (project…)", InstallTarget.OhMyPiProject, true));
            installItem.DropDownItems.Add(CreateInstallItem("Claude Code", InstallTarget.ClaudeCode, false));
            installItem.DropDownItems.Add(CreateInstallItem("Cursor", InstallTarget.Cursor, false));
            installItem.DropDownItems.Add(CreateInstallItem("VS Code (project…)", InstallTarget.VsCode, true));
            installItem.DropDownItems.Add(CreateInstallItem("Codex", InstallTarget.Codex, false));

            rootItem = new ToolStripMenuItem(Caption);
            rootItem.DropDownItems.Add(enabledItem);
            rootItem.DropDownItems.Add(allowMutationsItem);
            rootItem.DropDownItems.Add(new ToolStripSeparator());
            rootItem.DropDownItems.Add(statusItem);
            rootItem.DropDownItems.Add(copyEndpointItem);
            rootItem.DropDownItems.Add(copyTokenItem);
            rootItem.DropDownItems.Add(copyConfigItem);
            rootItem.DropDownItems.Add(new ToolStripSeparator());
            rootItem.DropDownItems.Add(installItem);

            menuStrip.Items.Add(rootItem);

            Refresh();
        }

        public void Detach()
        {
            if (statusForm != null)
            {
                if (!statusForm.IsDisposed)
                {
                    statusForm.Close();
                }

                statusForm = null;
            }

            if (rootItem == null)
            {
                return;
            }

            menuStrip.Items.Remove(rootItem);

            rootItem.Dispose();

            rootItem = null;
            enabledItem = null;
            allowMutationsItem = null;
            copyEndpointItem = null;
        }

        public void Refresh()
        {
            if (rootItem == null)
            {
                return;
            }

            if (menuStrip.InvokeRequired)
            {
                menuStrip.BeginInvoke(new Action(Refresh));
                return;
            }

            var running = isRunning();
            var url = endpointUrl();

            rootItem.Text = running ? $"{Caption} ({DescribeEndpoint(url)})" : $"{Caption} (stopped)";
            enabledItem.Checked = running;
            allowMutationsItem.Checked = getAllowMutations();
            copyEndpointItem.Enabled = !string.IsNullOrEmpty(url);
        }

        private static string DescribeEndpoint(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "no endpoint";
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Authority;
            }

            return url;
        }

        private static bool IsOhMyPi(InstallTarget target)
        {
            return target == InstallTarget.OhMyPiUser || target == InstallTarget.OhMyPiProject;
        }

        private ToolStripMenuItem CreateInstallItem(string label, InstallTarget target, bool needsDirectory)
        {
            return new ToolStripMenuItem(label, null, (sender, e) => RunInstall(label, target, needsDirectory));
        }

        private void ToggleEnabled()
        {
            try
            {
                if (isRunning())
                {
                    stop();
                }
                else
                {
                    start();
                }
            }
            catch (Exception ex)
            {
                Program.ShowException(ex);
            }

            Refresh();
        }

        private void ToggleAllowMutations()
        {
            try
            {
                setAllowMutations(!getAllowMutations());
            }
            catch (Exception ex)
            {
                Program.ShowException(ex);
            }

            Refresh();
        }

        private void ShowStatus()
        {
            if (statusForm != null && !statusForm.IsDisposed)
            {
                statusForm.Activate();
                return;
            }

            var form = new McpStatusForm(
                serverName(),
                endpointUrl(),
                isRunning(),
                getAllowMutations(),
                ProtocolVersions.Advertised,
                PluginVersion.Value,
                token(),
                describeEntry());

            form.FormClosed += (sender, e) => statusForm = null;

            statusForm = form;

            var owner = menuStrip.FindForm();
            if (owner != null)
            {
                form.Show(owner);
            }
            else
            {
                form.Show();
            }
        }

        private void RunInstall(string label, InstallTarget target, bool needsDirectory)
        {
            if (string.IsNullOrEmpty(endpointUrl()))
            {
                MessageBox.Show("The server is not running, so there is no endpoint to write. Enable it first with MCP Server > Enabled.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var owner = menuStrip.FindForm();

            string directory = null;
            if (needsDirectory)
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = target == InstallTarget.VsCode
                        ? "Choose the project directory. The MCP configuration is written to .vscode\\mcp.json inside it."
                        : "Choose the project directory. The MCP configuration is written to .omp\\mcp.json inside it.";
                    dialog.ShowNewFolderButton = false;

                    if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath))
                    {
                        return;
                    }

                    directory = dialog.SelectedPath;
                }
            }

            InstallResult result;
            try
            {
                result = install(target, directory);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception ex)
            {
                Program.ShowException(ex);
                return;
            }

            var message = result != null && !string.IsNullOrEmpty(result.Message) ? result.Message : $"Configuration written for {label}.";
            if (IsOhMyPi(target) && message.IndexOf("/mcp reload", StringComparison.OrdinalIgnoreCase) < 0)
            {
                message = $"{message}{Environment.NewLine}{Environment.NewLine}Run /mcp reload then /mcp test {serverName()} in oh-my-pi.";
            }

            MessageBox.Show(message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
