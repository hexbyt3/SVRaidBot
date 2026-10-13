using System;
using SysBot.Base;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    public class UpdateForm : Form
    {
        private Button buttonDownload = null!;
        private Label labelUpdateInfo = null!;
        private readonly Label labelChangelogTitle = new();
        private TextBox textBoxChangelog = null!;
        private readonly bool isUpdateRequired;
        private readonly bool isUpdateAvailable;
        private readonly string newVersion;

        public UpdateForm(bool updateRequired, string newVersion, bool updateAvailable)
        {
            isUpdateRequired = updateRequired;
            this.newVersion = newVersion;
            isUpdateAvailable = updateAvailable;
            InitializeComponent();
            Load += async (sender, e) => await FetchAndDisplayChangelog();
            UpdateFormText();
        }

        private void InitializeComponent()
        {
            labelUpdateInfo = new Label();
            buttonDownload = new Button();

            ClientSize = new Size(500, 300);

            labelUpdateInfo.AutoSize = true;
            labelUpdateInfo.Location = new Point(12, 20);
            labelUpdateInfo.Size = new Size(460, 60);

            if (isUpdateRequired)
            {
                labelUpdateInfo.Text = "A required update is available. You must update to continue using this application.";
                ControlBox = false;
            }
            else if (isUpdateAvailable)
            {
                labelUpdateInfo.Text = "A new version is available. Please download the latest version.";
            }
            else
            {
                labelUpdateInfo.Text = "You are on the latest version. You can re-download if needed.";
                buttonDownload.Text = "Re-Download Latest Version";
            }

            buttonDownload.Size = new Size(130, 23);
            int buttonX = (ClientSize.Width - buttonDownload.Size.Width) / 2;
            int buttonY = ClientSize.Height - buttonDownload.Size.Height - 20;
            buttonDownload.Location = new Point(buttonX, buttonY);
            if (string.IsNullOrEmpty(buttonDownload.Text))
            {
                buttonDownload.Text = "Download Update";
            }
            buttonDownload.Click += ButtonDownload_Click;

            labelChangelogTitle.AutoSize = true;
            labelChangelogTitle.Location = new Point(10, 60);
            labelChangelogTitle.Size = new Size(70, 15);
            labelChangelogTitle.Font = new Font(labelChangelogTitle.Font.FontFamily, 11, FontStyle.Bold);
            labelChangelogTitle.Text = $"Changelog ({newVersion}):";

            textBoxChangelog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(10, 90),
                Size = new Size(480, 150),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right
            };

            Controls.Add(labelUpdateInfo);
            Controls.Add(buttonDownload);
            Controls.Add(labelChangelogTitle);
            Controls.Add(textBoxChangelog);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "UpdateForm";
            StartPosition = FormStartPosition.CenterScreen;
            UpdateFormText();
        }

        private void UpdateFormText()
        {
            if (isUpdateAvailable)
            {
                Text = $"Update Available ({newVersion})";
            }
            else
            {
                Text = "Re-Download Latest Version";
            }
        }

        private async Task FetchAndDisplayChangelog()
        {
            textBoxChangelog.Text = await UpdateChecker.FetchChangelogAsync();
        }

        private async void ButtonDownload_Click(object? sender, EventArgs e)
        {
            buttonDownload.Enabled = false;
            buttonDownload.Text = "Downloading...";

            try
            {
                var latest = await UpdateChecker.GetLatestAsync(refresh: true);
                if (latest?.DownloadUrl == null)
                {
                    MessageBox.Show("Failed to fetch the download URL. Please check your internet connection and try again.",
                        "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                await UpdateInstaller.InstallAsync(latest);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Update failed: {ex.Message}", "Update");
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed)
                {
                    buttonDownload.Enabled = true;
                    buttonDownload.Text = isUpdateAvailable ? "Download Update" : "Re-Download Latest Version";
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (isUpdateRequired && e.CloseReason == CloseReason.UserClosing && !Main.IsUpdating)
            {
                var answer = MessageBox.Show("This update is required. SVRaidBot will close if you skip it. Close SVRaidBot now?",
                    "Update Required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                    e.Cancel = true;
            }
        }
    }
}