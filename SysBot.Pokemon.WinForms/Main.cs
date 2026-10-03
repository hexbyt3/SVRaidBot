using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.ComponentModel;

namespace SysBot.Pokemon.WinForms
{
    public sealed partial class Main : Form
    {
        private readonly List<PokeBotState> Bots = new();
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ProgramConfig Config { get; set; }
        private IPokeBotRunner RunningEnvironment { get; set; }
        public readonly ISwitchConnectionAsync? SwitchConnection;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public static bool IsUpdating { get; set; } = false;
        private System.Windows.Forms.Timer _autoSaveTimer;
        private System.Windows.Forms.Timer _updateCheckTimer;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cts;
        private bool hasUpdate = false;
        private double pulsePhase = 0;
        private Color lastIndicatorColor = Color.Empty;
        private DateTime lastIndicatorUpdate = DateTime.MinValue;
        private const int PULSE_UPDATE_INTERVAL_MS = 50; 

        public Main()
        {
            InitializeComponent();
            Load += async (sender, e) => await InitializeAsync();

            // Initialize dummy controls for compatibility
            TC_Main = new TabControl { Visible = false };
            Tab_Bots = new TabPage();
            Tab_Hub = new TabPage();
            Tab_Logs = new TabPage();
            TC_Main.TabPages.AddRange(new[] { Tab_Bots, Tab_Hub, Tab_Logs });
            comboBox1 = new ComboBox { Visible = false };
            TC_Main.SendToBack();
            comboBox1.SendToBack();
        }

        private async Task InitializeAsync()
        {
            if (IsUpdating)
                return;
            string discordName = string.Empty;

            // Update checker - check silently on startup; a required update blocks loading any bot
            try
            {
                var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync(forceShow: false);
                hasUpdate = updateAvailable;
                if (updateRequired)
                {
                    ShowRequiredUpdate(newVersion);
                    return;
                }
            }
            catch { /* Ignore update check errors on startup */ }

            if (File.Exists(Program.ConfigPath))
            {
                // A file another program holds open is not a corrupt one: replacing it
                // with the backup or with defaults would throw away a good config.
                var lines = ReadConfigText();
                if (lines == null)
                {
                    WinFormsUtil.Error("config.json is open in another program and could not be read.",
                        "Close whatever has it open (an editor, antivirus scan or sync tool), then start the bot again. Nothing was changed.");
                    isExiting = true;
                    Application.Exit();
                    return;
                }

                try
                {
                    Config = JsonSerializer.Deserialize(lines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();
                }
                catch (JsonException ex)
                {
                    LogUtil.LogError($"Config corrupted, trying backup: {ex.Message}", "Config");
                    Config = LoadBackupConfig();
                }

                LogConfig.MaxArchiveFiles = Config.Hub.MaxArchiveFiles;
                LogConfig.LoggingEnabled = Config.Hub.LoggingEnabled;

                RunningEnvironment = GetRunner(Config);
                var first = Config.Bots.FirstOrDefault();
                if (first != null)
                {
                    first.Initialize();
                    AddBot(first);
                }
                if (Config.Bots.Length > 1)
                {
                    var dropped = string.Join(Environment.NewLine, Config.Bots.Skip(1).Select(b => b.Connection.ToString()));
                    WinFormsUtil.Alert($"This copy of SVRaidBot now hosts only {first!.Connection}.",
                        PokeBotRunner<PK9>.OneSwitchPerProgram + Environment.NewLine + Environment.NewLine +
                        "These Switches were removed from this copy:" + Environment.NewLine + dropped);
                }
            }
            else
            {
                Config = new ProgramConfig();
                RunningEnvironment = GetRunner(Config);
            }

            LoadControls();
            Text = $"{(string.IsNullOrEmpty(Config.Hub.BotName) ? "S/V RaidBot" : Config.Hub.BotName)} {SVRaidBot.Version} ({Config.Mode})";
            trayIcon.Text = (Config?.Hub?.BotName != null && !string.IsNullOrEmpty(Config.Hub.BotName)) ? Config.Hub.BotName : "S/V RaidBot";
            _ = Task.Run(BotMonitor);
            InitUtil.InitializeStubs(Config.Mode);
            // Start periodic update checks
            StartUpdateCheckTimer();

            LogUtil.LogInfo($"Bot initialization complete", "System");
            _ = Task.Run(() =>
            {
                try
                {
                    this.InitWebServer();
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "System");
                }
            });
        }

        private void RTB_Logs_TextChanged(object sender, EventArgs e)
        {
            RTB_Logs.Invalidate();
            RTB_Logs.Update();
        }

        private static IPokeBotRunner GetRunner(ProgramConfig cfg) => cfg.Mode switch
        {
            ProgramMode.SV => new PokeBotRunnerImpl<PK9>(cfg.Hub, new BotFactory9SV()),
            _ => throw new IndexOutOfRangeException("Unsupported mode."),
        };

        private async Task BotMonitor()
        {
            while (!Disposing)
            {
                try
                {
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.ReadState();
                }
                catch
                {
                    // Updating the collection by adding/removing bots will change the iterator
                    // Can try a for-loop or ToArray, but those still don't prevent concurrent mutations of the array.
                    // Just try, and if failed, ignore. Next loop will be fine. Locks on the collection are kinda overkill, since this task is not critical.
                }
                await Task.Delay(2_000).ConfigureAwait(false);
            }
        }

        private void LoadControls()
        {
            MinimumSize = Size;
            PG_Hub.SelectedObject = RunningEnvironment.Config;
            _autoSaveTimer = new System.Windows.Forms.Timer
            {
                Interval = 10_000,
                Enabled = true
            };
            _autoSaveTimer.Tick += (s, e) => SaveCurrentConfig();
            var routines = ((PokeRoutineType[])Enum.GetValues(typeof(PokeRoutineType))).Where(z => RunningEnvironment.SupportsRoutine(z));
            var list = routines.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Routine.DisplayMember = nameof(ComboItem.Text);
            CB_Routine.ValueMember = nameof(ComboItem.Value);
            CB_Routine.DataSource = list;
            CB_Routine.SelectedValue = (int)PokeRoutineType.RotatingRaidBot; // default option

            var protocols = (SwitchProtocol[])Enum.GetValues(typeof(SwitchProtocol));
            var listP = protocols.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Protocol.DisplayMember = nameof(ComboItem.Text);
            CB_Protocol.ValueMember = nameof(ComboItem.Value);
            CB_Protocol.DataSource = listP;
            CB_Protocol.SelectedIndex = (int)SwitchProtocol.WiFi; // default option

            LogUtil.Forwarders.Add(AppendLog);
        }

        private void AppendLog(string message, string identity)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] - {identity}: {message}{Environment.NewLine}";
            if (InvokeRequired)
                Invoke((MethodInvoker)(() => UpdateLog(line)));
            else
                UpdateLog(line);
        }

        private void UpdateLog(string line)
        {
            // ghetto truncate
            if (RTB_Logs.Lines.Length > 99_999)
                RTB_Logs.Lines = RTB_Logs.Lines.Skip(25_0000).ToArray();

            RTB_Logs.AppendText(line);
            RTB_Logs.ScrollToCaret();
        }

        private ProgramConfig GetCurrentConfiguration()
        {
            if (Config == null)
            {
                throw new InvalidOperationException("Config has not been initialized because a valid license was not entered.");
            }
            Config.Bots = Bots.ToArray();
            return Config;
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            // If not exiting, minimize to tray instead. An update closes the program
            // for real, so it still stops the bots and saves like any other exit.
            if (!isExiting && !IsUpdating)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(2000, "S/V RaidBot", "Application minimized to system tray", ToolTipIcon.Info);
                return;
            }
            this.StopWebServer();

            // Delete the port info file
            try
            {
                string portInfoPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), $"SVRaidBot_{Environment.ProcessId}.port");
                if (File.Exists(portInfoPath))
                    File.Delete(portInfoPath);
            }
            catch { /* Ignore cleanup errors */ }

            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Dispose();
            }

            if (_updateCheckTimer != null)
            {
                _updateCheckTimer.Stop();
                _updateCheckTimer.Dispose();
            }

            if (animationTimer != null)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            // Create a backup copy of config
            try
            {
                if (File.Exists(Program.ConfigPath))
                {
                    File.Copy(Program.ConfigPath, Program.ConfigPath + ".backup", true);
                }
            }
            catch { }
            if (Config != null)
                SaveCurrentConfig();
            var bots = RunningEnvironment;
            if (bots == null || !bots.IsRunning)
                return;

            async Task WaitUntilNotRunning()
            {
                while (bots.IsRunning)
                    await Task.Delay(10).ConfigureAwait(false);
            }

            // Try to let all bots hard-stop before ending execution of the entire program.
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            // Hard stop gives claimed GenPKM requests back and waits up to 5 s for those reports to send.
            bots.StopAll();
            Task.WhenAny(WaitUntilNotRunning(), Task.Delay(10_000)).ConfigureAwait(true).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Loads config.json.backup (written at every clean exit). When no backup
        /// parses, the corrupt config is moved aside rather than left for the next
        /// autosave to overwrite, so its token and raid list can still be recovered by hand.
        /// </summary>
        private static string? ReadConfigText()
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(Program.ConfigPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 5)
                    {
                        LogUtil.LogError($"Could not read config.json: {ex.Message}", "Config");
                        return null;
                    }
                    Thread.Sleep(300 * attempt);
                }
            }
        }

        private static ProgramConfig LoadBackupConfig()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(Program.ConfigPath))!;
            var candidates = new[] { Program.ConfigPath + ".backup" }
                .Concat(Directory.GetFiles(dir, "*.backup_*").OrderByDescending(f => new FileInfo(f).LastWriteTime))
                .Where(File.Exists);

            foreach (var backup in candidates)
            {
                try
                {
                    var backupLines = File.ReadAllText(backup);
                    var restored = JsonSerializer.Deserialize(backupLines, ProgramConfigContext.Default.ProgramConfig);
                    if (restored == null)
                        continue;
                    File.WriteAllText(Program.ConfigPath, backupLines);
                    LogUtil.LogInfo($"Restored config from {Path.GetFileName(backup)}", "Config");
                    return restored;
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Backup {Path.GetFileName(backup)} is unreadable too: {ex.Message}", "Config");
                }
            }

            var aside = $"{Program.ConfigPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(Program.ConfigPath, aside);
                LogUtil.LogError($"No valid backup found, using new config. The damaged one was kept as {Path.GetFileName(aside)}.", "Config");
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"No valid backup found, using new config. Could not keep the damaged one: {ex.Message}", "Config");
            }
            return new ProgramConfig();
        }

        private readonly object _saveLock = new();

        private void SaveCurrentConfig()
        {
            lock (_saveLock)
                SaveConfigLocked();
        }

        private void SaveConfigLocked()
        {
            try
            {
                var cfg = GetCurrentConfiguration();
                string lines;
                lock (RaidListSync.Gate)
                    lines = JsonSerializer.Serialize(cfg, ProgramConfigContext.Default.ProgramConfig);

                string tempPath = Program.ConfigPath + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(lines);
                    stream.Write(bytes);
                    stream.Flush(true);
                }

                // Antivirus and sync tools hold config.json open for a moment after
                // each write, and replacing it then fails with "Access denied".
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tempPath, Program.ConfigPath, true);
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                    {
                        Thread.Sleep(200 * attempt);
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Failed to save config: {ex.Message}", "Config");
            }
        }

        [JsonSerializable(typeof(ProgramConfig))]
        [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
        public sealed partial class ProgramConfigContext : JsonSerializerContext
        { }

        private void B_Start_Click(object sender, EventArgs e)
        {
            // Reset error state when starting
            SV.BotRaid.RotatingRaidBotSV.HasErrored = false;

            SaveCurrentConfig();

            LogUtil.LogInfo("Starting all bots...", "Form");
            RunningEnvironment.InitializeStart();
            SendAll(BotControlCommand.Start);
            btnNavLogs.PerformClick(); // Switch to logs view

            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been started.");
        }

        private void B_RebootAndStop_Click(object sender, EventArgs e)
        {
            B_Stop_Click(sender, e);
            Task.Run(async () =>
            {
                await Task.Delay(3_500).ConfigureAwait(false);
                SaveCurrentConfig();
                LogUtil.LogInfo("Restarting all the consoles...", "Form");
                RunningEnvironment.InitializeStart();
                SendAll(BotControlCommand.RebootAndStop);
                Invoke((MethodInvoker)(() => btnNavLogs.PerformClick())); // Switch to logs view
                if (Bots.Count == 0)
                    WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the reboot command.");
            });
        }

        private async void Updater_Click(object sender, EventArgs e)
        {
            var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync(forceShow: true);
            hasUpdate = updateAvailable; // Update the indicator
        }

        private void StartUpdateCheckTimer()
        {
            _updateCheckTimer = new System.Windows.Forms.Timer
            {
                Interval = 3600000, // Check every hour
                Enabled = true
            };
            _updateCheckTimer.Tick += async (s, e) =>
            {
                try
                {
                    var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync(forceShow: false);
                    hasUpdate = updateAvailable;
                    if (updateRequired && !_requiredUpdateShown)
                    {
                        LogUtil.LogInfo($"Required update {newVersion} is out. Stopping the bots until it is installed.", "Update");
                        RunningEnvironment.StopAll();
                        ShowRequiredUpdate(newVersion);
                    }
                }
                catch { /* Ignore update check errors */ }
            };
        }

        private bool _requiredUpdateShown;

        private void ShowRequiredUpdate(string newVersion)
        {
            _requiredUpdateShown = true;
            using (var form = new UpdateForm(true, newVersion, true))
                form.ShowDialog(this);
            // Either the installer is taking over or the host skipped a required update.
            isExiting = true;
            Application.Exit();
        }

        private void RefreshMap_Click(object sender, EventArgs e)
        {
            SaveCurrentConfig();
            LogUtil.LogInfo("Sending RefreshMap command to all bots.", "Refresh Map");
            SendAll(BotControlCommand.RefreshMap);
            btnNavLogs.PerformClick(); // Switch to logs view
            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the refresh map command.");
        }

        private void SendAll(BotControlCommand cmd)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.SendCommand(cmd, false);

            LogUtil.LogText($"All bots have been issued a command to {cmd}.");
        }

        private void B_Stop_Click(object sender, EventArgs e)
        {
            var env = RunningEnvironment;
            if (!env.IsRunning && (ModifierKeys & Keys.Alt) == 0)
            {
                WinFormsUtil.Alert("Nothing is currently running.");
                return;
            }

            var cmd = BotControlCommand.Stop;

            if ((ModifierKeys & Keys.Control) != 0 || (ModifierKeys & Keys.Shift) != 0) // either, because remembering which can be hard
            {
                if (env.IsRunning)
                {
                    WinFormsUtil.Alert("Commanding all bots to Idle.", "Press Stop (without a modifier key) to hard-stop and unlock control, or press Stop with the modifier key again to resume.");
                    cmd = BotControlCommand.Idle;
                }
                else
                {
                    WinFormsUtil.Alert("Commanding all bots to resume their original task.", "Press Stop (without a modifier key) to hard-stop and unlock control.");
                    cmd = BotControlCommand.Resume;
                }
            }
            SendAll(cmd);
        }

        private void B_New_Click(object sender, EventArgs e)
        {
            if (Bots.Count > 0)
            {
                WinFormsUtil.Alert(PokeBotRunner<PK9>.OneSwitchPerProgram, "Remove the current bot first to switch this copy to a different Switch.");
                return;
            }
            var cfg = CreateNewBotConfig();
            if (!AddBot(cfg))
            {
                WinFormsUtil.Alert("Unable to add bot; ensure details are valid and not duplicate with an already existing bot.");
                return;
            }
            System.Media.SystemSounds.Asterisk.Play();
        }

        private bool AddBot(PokeBotState cfg)
        {
            if (!cfg.IsValid())
                return false;

            // Disallow duplicate routines.
            if (Bots.Any(z => z.Connection.Equals(cfg.Connection) && cfg.NextRoutineType == z.NextRoutineType))
                return false;

            PokeRoutineExecutorBase newBot;
            try
            {
                Console.WriteLine($"Current Mode ({Config.Mode}) does not support this type of bot ({cfg.CurrentRoutineType}).");
                newBot = RunningEnvironment.CreateBotFromConfig(cfg);
            }
            catch
            {
                return false;
            }

            try
            {
                RunningEnvironment.Add(newBot);
            }
            catch (ArgumentException ex)
            {
                WinFormsUtil.Error(ex.Message);
                return false;
            }

            AddBotControl(cfg);
            Bots.Add(cfg);
            return true;
        }

        private void AddBotControl(PokeBotState cfg)
        {
            var row = new BotController { Width = FLP_Bots.Width - 60 };
            row.Initialize(RunningEnvironment, cfg);
            FLP_Bots.Controls.Add(row);
            FLP_Bots.SetFlowBreak(row, true);

            // Modern dark theme styling is now handled in BotController itself

            row.Click += (s, e) =>
            {
                var details = cfg.Connection;
                TB_IP.Text = details.IP;
                NUD_Port.Value = details.Port;
                CB_Protocol.SelectedIndex = (int)details.Protocol;
                CB_Routine.SelectedValue = (int)cfg.InitialRoutine;
            };

            row.Remove += (s, e) =>
            {
                Bots.Remove(row.State);
                RunningEnvironment.Remove(row.State, !RunningEnvironment.Config.SkipConsoleBotCreation);
                FLP_Bots.Controls.Remove(row);
            };
        }

        private PokeBotState CreateNewBotConfig()
        {
            var ip = TB_IP.Text;
            var port = (int)NUD_Port.Value;
            var cfg = BotConfigUtil.GetConfig<SwitchConnectionConfig>(ip, port);
            cfg.Protocol = (SwitchProtocol)WinFormsUtil.GetIndex(CB_Protocol);

            var pk = new PokeBotState { Connection = cfg };
            var type = (PokeRoutineType)WinFormsUtil.GetIndex(CB_Routine);
            pk.Initialize(type);
            return pk;
        }

        private void FLP_Bots_Resize(object sender, EventArgs e)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.Width = FLP_Bots.Width - 60; // Account for scrollbar and padding
        }

        private void CB_Protocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            TB_IP.Visible = CB_Protocol.SelectedIndex == 0;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            trayIcon.Visible = false;
            BringToFront();
            Activate();

            int headerHeight = headerPanel.Height + 10;

            // Adjust padding only if panels haven't been initialized yet (checking Padding.Top as an indicator)
            if (hubPanel.Padding.Top <= 40)
                hubPanel.Padding = new Padding(40, headerHeight, 40, 40);

            if (logsPanel.Padding.Top <= 40)
                logsPanel.Padding = new Padding(40, headerHeight, 40, 40);

            // Force layout recalculation explicitly
            hubPanel.PerformLayout();
            PG_Hub.Refresh();

            logsPanel.PerformLayout();
            RTB_Logs.Refresh();

            // Ensure control buttons are properly positioned
            HeaderPanel_Resize(headerPanel, EventArgs.Empty);
        }

        private void ExitApplication()
        {
            var bots = RunningEnvironment;
            if (bots != null && bots.IsRunning)
            {
                var result = MessageBox.Show(
                    "Bots are currently running. Do you want to stop all bots and exit?",
                    "Exit Confirmation",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                    return;

                bots.StopAll();
            }

            isExiting = true;
            trayIcon.Dispose();
            Application.Exit();
        }
    }

    // Extension methods for graphics
    public static class GraphicsExtensions
    {
        public static void AddRoundedRectangle(this GraphicsPath path, Rectangle rect, int radius)
        {
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
        }
    }
}