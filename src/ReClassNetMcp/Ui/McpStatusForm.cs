using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ReClassNET.Forms;
using ReClassNET.UI;

namespace ReClassNetMcp.Ui
{
    internal sealed class McpStatusForm : IconForm
    {
        private const string Caption = "MCP Server";

        private readonly string tokenValue;

        private readonly string configValue;

        private readonly Font monoSpaceFont;

        private readonly TextBox tokenBox;

        private readonly Button tokenVisibilityButton;

        public McpStatusForm(string serverName, string url, bool running, bool allowMutations, string protocolVersion, string pluginVersion, string token, string configSnippet)
        {
            tokenValue = token ?? string.Empty;
            configValue = NormalizeLineEndings(configSnippet);

            monoSpaceFont = new Font(FontFamily.GenericMonospace, 9f);

            Text = "MCP Server Status";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(520, 360);
            ClientSize = new Size(580, 430);

            var infoPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 6,
                Margin = new Padding(0, 0, 0, 8)
            };

            infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            AddInfoRow(infoPanel, "Endpoint:", string.IsNullOrEmpty(url) ? "(not listening)" : url);
            AddInfoRow(infoPanel, "Server name:", serverName ?? string.Empty);
            AddInfoRow(infoPanel, "State:", running ? "running" : "stopped");
            AddInfoRow(infoPanel, "Mutations:", allowMutations ? "allowed" : "blocked");
            AddInfoRow(infoPanel, "Protocol version:", protocolVersion ?? string.Empty);
            AddInfoRow(infoPanel, "Plugin version:", pluginVersion ?? string.Empty);

            var hasToken = tokenValue.Length != 0;

            tokenBox = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                Text = hasToken ? tokenValue : "(none)",
                UseSystemPasswordChar = hasToken,
                Margin = new Padding(0, 3, 6, 3)
            };

            tokenVisibilityButton = new Button
            {
                Text = "Show",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(72, 0),
                Anchor = AnchorStyles.Left,
                Enabled = hasToken
            };

            tokenVisibilityButton.Click += TokenVisibilityButtonClick;

            var tokenCopyButton = new Button
            {
                Text = "Copy",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(72, 0),
                Anchor = AnchorStyles.Left,
                Enabled = hasToken
            };

            tokenCopyButton.Click += TokenCopyButtonClick;

            var tokenLabel = new Label
            {
                Text = "Token:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 12, 3)
            };

            var tokenPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 10)
            };

            tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tokenPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tokenPanel.Controls.Add(tokenLabel, 0, 0);
            tokenPanel.Controls.Add(tokenBox, 1, 0);
            tokenPanel.Controls.Add(tokenVisibilityButton, 2, 0);
            tokenPanel.Controls.Add(tokenCopyButton, 3, 0);

            var configLabel = new Label
            {
                Text = "Client configuration:",
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Margin = new Padding(3, 3, 3, 6)
            };

            var configCopyButton = new Button
            {
                Text = "Copy",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(72, 0),
                Anchor = AnchorStyles.Right
            };

            configCopyButton.Click += ConfigCopyButtonClick;

            var configHeaderPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1
            };

            configHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            configHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            configHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            configHeaderPanel.Controls.Add(configLabel, 0, 0);
            configHeaderPanel.Controls.Add(configCopyButton, 1, 0);

            var configBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = monoSpaceFont,
                Text = configValue,
                Margin = new Padding(3, 0, 3, 10)
            };

            var closeButton = new Button
            {
                Text = "Close",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(80, 0),
                Anchor = AnchorStyles.Right
            };

            closeButton.Click += CloseButtonClick;

            var closePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 1
            };

            closePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            closePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            closePanel.Controls.Add(closeButton, 0, 0);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 5
            };

            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(infoPanel, 0, 0);
            root.Controls.Add(tokenPanel, 0, 1);
            root.Controls.Add(configHeaderPanel, 0, 2);
            root.Controls.Add(configBox, 0, 3);
            root.Controls.Add(closePanel, 0, 4);

            Controls.Add(root);

            CancelButton = closeButton;
        }

        internal static void CopyToClipboard(string text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show($"There is no {what} to copy.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                MessageBox.Show("The clipboard is locked by another application. Try again in a moment.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            GlobalWindowManager.AddWindow(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            GlobalWindowManager.RemoveWindow(this);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                monoSpaceFont.Dispose();
            }
        }

        private void AddInfoRow(TableLayoutPanel panel, string caption, string value)
        {
            var row = panel.RowStyles.Count;

            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var captionLabel = new Label
            {
                Text = caption,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 4, 12, 4)
            };

            var valueLabel = new Label
            {
                Text = value,
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Height = Font.Height + 3,
                Margin = new Padding(0, 4, 3, 4)
            };

            panel.Controls.Add(captionLabel, 0, row);
            panel.Controls.Add(valueLabel, 1, row);
        }

        private static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        private void TokenVisibilityButtonClick(object sender, EventArgs e)
        {
            tokenBox.UseSystemPasswordChar = !tokenBox.UseSystemPasswordChar;
            tokenVisibilityButton.Text = tokenBox.UseSystemPasswordChar ? "Show" : "Hide";
        }

        private void TokenCopyButtonClick(object sender, EventArgs e)
        {
            CopyToClipboard(tokenValue, "token");
        }

        private void ConfigCopyButtonClick(object sender, EventArgs e)
        {
            CopyToClipboard(configValue, "configuration");
        }

        private void CloseButtonClick(object sender, EventArgs e)
        {
            Close();
        }
    }
}
