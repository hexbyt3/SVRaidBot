using SysBot.Pokemon.WinForms.Properties;
using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Linq;
using System;

namespace SysBot.Pokemon.WinForms
{
    partial class Main
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            if (disposing && trayIcon != null)
            {
                trayIcon.Dispose();
            }
            if (disposing && _logoBrush != null)
            {
                _logoBrush.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Main));

            // Initialize timer for animations
            animationTimer = new System.Windows.Forms.Timer(this.components);
            animationTimer.Interval = 16; // Back to 60fps for smooth animations
            animationTimer.Tick += AnimationTimer_Tick;

            // Initialize tray icon
            trayIcon = new NotifyIcon(this.components);
            trayContextMenu = new ContextMenuStrip(this.components);

            // Main Panel Structure
            mainLayoutPanel = new TableLayoutPanel();
            sidebarPanel = new Panel();
            contentPanel = new Panel();
            headerPanel = new Panel();

            // Sidebar Components
            logoPanel = new Panel();
            navButtonsPanel = new FlowLayoutPanel();
            btnNavBots = new Button();
            btnNavHub = new Button();
            btnNavLogs = new Button();
            btnNavExit = new Button();
            sidebarBottomPanel = new Panel();
            btnUpdate = new Button();
            statusIndicator = new Panel();

            // Header Components
            titleLabel = new Label();
            controlButtonsPanel = new FlowLayoutPanel();
            btnStart = new Button();
            btnStop = new Button();
            btnReboot = new Button();
            btnRefreshMap = new Button();

            // Content Panels
            botsPanel = new Panel();
            hubPanel = new Panel();
            logsPanel = new Panel();

            // Bots Panel Components
            botHeaderPanel = new Panel();
            addBotPanel = new Panel();
            TB_IP = new TextBox();
            NUD_Port = new NumericUpDown();
            CB_Protocol = new ComboBox();
            CB_Routine = new ComboBox();
            B_New = new Button();
            FLP_Bots = new FlowLayoutPanel();

            // Hub Panel Components
            PG_Hub = new PropertyGrid();

            // Logs Panel Components
            RTB_Logs = new RichTextBox();
            logsHeaderPanel = new Panel();
            logSearchBox = new TextBox();
            searchStatusLabel = new Label();
            btnClearLogs = new Button();

            mainLayoutPanel.SuspendLayout();
            sidebarPanel.SuspendLayout();
            navButtonsPanel.SuspendLayout();
            sidebarBottomPanel.SuspendLayout();
            headerPanel.SuspendLayout();
            controlButtonsPanel.SuspendLayout();
            contentPanel.SuspendLayout();
            botsPanel.SuspendLayout();
            botHeaderPanel.SuspendLayout();
            addBotPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)NUD_Port).BeginInit();
            hubPanel.SuspendLayout();
            logsPanel.SuspendLayout();
            logsHeaderPanel.SuspendLayout();
            SuspendLayout();

            // Form
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1280, 720);
            MinimumSize = new Size(1100, 600);
            BackColor = Color.FromArgb(23, 26, 33);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            Icon = Resources.icon;
            Name = "Main";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "RaidBot Control Center";
            FormClosing += Main_FormClosing;
            Shown += Main_Shown;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.DoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            // Main Layout Panel
            mainLayoutPanel.ColumnCount = 2;
            mainLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260F));
            mainLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            mainLayoutPanel.Controls.Add(sidebarPanel, 0, 0);
            mainLayoutPanel.Controls.Add(contentPanel, 1, 0);
            mainLayoutPanel.Dock = DockStyle.Fill;
            mainLayoutPanel.Location = new Point(0, 0);
            mainLayoutPanel.Margin = new Padding(0);
            mainLayoutPanel.Name = "mainLayoutPanel";
            mainLayoutPanel.RowCount = 1;
            mainLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayoutPanel.TabIndex = 0;
            mainLayoutPanel.BackColor = Color.Transparent;

            // Sidebar Panel
            sidebarPanel.BackColor = Color.FromArgb(18, 18, 18);
            sidebarPanel.Controls.Add(navButtonsPanel);
            sidebarPanel.Controls.Add(sidebarBottomPanel);
            sidebarPanel.Controls.Add(logoPanel);
            sidebarPanel.Dock = DockStyle.Fill;
            sidebarPanel.Location = new Point(0, 0);
            sidebarPanel.Margin = new Padding(0);
            sidebarPanel.Name = "sidebarPanel";
            sidebarPanel.Size = new Size(260, 720);
            sidebarPanel.TabIndex = 0;

            // Logo Panel
            logoPanel.BackColor = Color.FromArgb(15, 15, 15);
            logoPanel.Dock = DockStyle.Top;
            logoPanel.Height = 100;
            logoPanel.Location = new Point(0, 0);
            logoPanel.Name = "logoPanel";
            logoPanel.Size = new Size(260, 100);
            logoPanel.TabIndex = 2;
            logoPanel.Paint += LogoPanel_Paint;
            EnableDoubleBuffering(logoPanel);

            // Navigation Buttons Panel
            navButtonsPanel.AutoSize = false;
            navButtonsPanel.Controls.Add(btnNavBots);
            navButtonsPanel.Controls.Add(btnNavHub);
            navButtonsPanel.Controls.Add(btnNavLogs);
            navButtonsPanel.Controls.Add(btnNavExit);
            navButtonsPanel.Dock = DockStyle.Fill;
            navButtonsPanel.FlowDirection = FlowDirection.TopDown;
            navButtonsPanel.Location = new Point(0, 100);
            navButtonsPanel.Margin = new Padding(0);
            navButtonsPanel.Name = "navButtonsPanel";
            navButtonsPanel.Padding = new Padding(0, 40, 0, 0);
            navButtonsPanel.Size = new Size(260, 540);
            navButtonsPanel.TabIndex = 1;
            navButtonsPanel.BackColor = Color.Transparent;

            // Navigation Button Style
            ConfigureNavButton(btnNavBots, "BOTS", 0, "Manage bot connections");
            ConfigureNavButton(btnNavHub, "CONFIGURATION", 1, "System settings");
            ConfigureNavButton(btnNavLogs, "SYSTEM LOGS", 2, "View activity logs");
            ConfigureExitButton();

            // Sidebar Bottom Panel
            sidebarBottomPanel.SuspendLayout();
            sidebarBottomPanel.Controls.Add(btnUpdate);
            sidebarBottomPanel.Dock = DockStyle.Bottom;
            sidebarBottomPanel.Height = 80;
            sidebarBottomPanel.Location = new Point(0, 640);
            sidebarBottomPanel.Name = "sidebarBottomPanel";
            sidebarBottomPanel.Padding = new Padding(20, 10, 20, 20);
            sidebarBottomPanel.TabIndex = 0;
            sidebarBottomPanel.BackColor = Color.FromArgb(15, 15, 15);
            sidebarBottomPanel.ResumeLayout(false);

            statusIndicator.BackColor = Color.FromArgb(100, 100, 100); // Default gray
            statusIndicator.Size = new Size(12, 12);
            statusIndicator.Location = new Point(190, 10); // Fixed position from left
            statusIndicator.Name = "statusIndicator";
            statusIndicator.Enabled = false; // Prevent mouse interaction
            statusIndicator.Anchor = AnchorStyles.Top | AnchorStyles.Right; // Anchor to top-right
            CreateCircularRegion(statusIndicator);
            btnUpdate.Controls.Add(statusIndicator);
            statusIndicator.BringToFront();
            statusIndicator.Paint += (s, e) => {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = statusIndicator.ClientRectangle;
                rect.Inflate(-1, -1);

                using var brush = new SolidBrush(statusIndicator.BackColor);
                e.Graphics.FillEllipse(brush, rect);

                // Add inner highlight
                if (hasUpdate)
                {
                    var highlightRect = new Rectangle(2, 2, 4, 4);
                    using var highlightBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
                    e.Graphics.FillEllipse(highlightBrush, highlightRect);
                }
            };

            // Update Button
            btnUpdate.BackColor = Color.FromArgb(30, 30, 30);
            btnUpdate.Dock = DockStyle.Fill;
            btnUpdate.FlatAppearance.BorderSize = 0;
            btnUpdate.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 40, 40);
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            btnUpdate.ForeColor = Color.FromArgb(176, 176, 176);
            btnUpdate.Location = new Point(20, 10);
            btnUpdate.Name = "btnUpdate";
            btnUpdate.Size = new Size(220, 50);
            btnUpdate.TabIndex = 0;
            btnUpdate.Text = "CHECK FOR UPDATES";
            btnUpdate.UseVisualStyleBackColor = false;
            btnUpdate.Click += Updater_Click;
            btnUpdate.Cursor = Cursors.Hand;
            btnUpdate.Tag = new ButtonAnimationState();

            // Add tooltip
            var updateTooltip = new ToolTip();
            updateTooltip.SetToolTip(btnUpdate, "Check for updates");
            btnUpdate.MouseEnter += (s, e) => {
                if (hasUpdate)
                {
                    updateTooltip.SetToolTip(btnUpdate, "Update available! Click to download.");
                }
                else
                {
                    updateTooltip.SetToolTip(btnUpdate, "No updates available");
                }
            };

            // Reposition status indicator on resize
            btnUpdate.Resize += (s, e) => {
                if (statusIndicator != null && btnUpdate.Controls.Contains(statusIndicator))
                {
                    statusIndicator.Location = new Point(btnUpdate.ClientSize.Width - 25, 20);
                }
            };
            // Reposition status indicator on resize
            btnUpdate.Resize += (s, e) => {
                if (statusIndicator != null && btnUpdate.Controls.Contains(statusIndicator))
                {
                    statusIndicator.Location = new Point(btnUpdate.ClientSize.Width - 25, 20);
                }
            };
            btnUpdate.Layout += (s, e) => {
                if (statusIndicator != null && btnUpdate.Controls.Contains(statusIndicator))
                {
                    statusIndicator.Location = new Point(btnUpdate.ClientSize.Width - 25, 20);
                }
            };
            btnUpdate.Paint += (s, e) => {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                var animState = btnUpdate.Tag as ButtonAnimationState;

                // Draw hover glow background - only if hovering
                if (animState != null && animState.HoverProgress > 0 && animState.IsHovering)
                {
                    using var glowBrush = new SolidBrush(Color.FromArgb((int)(20 * animState.HoverProgress), 88, 101, 242));
                    e.Graphics.FillRectangle(glowBrush, btnUpdate.ClientRectangle);
                }

                // Determine icon color with hover effect
                var iconColor = btnUpdate.ForeColor;
                if (animState != null && animState.HoverProgress > 0)
                {
                    iconColor = Color.FromArgb(
                        (int)(176 + (224 - 176) * animState.HoverProgress),
                        (int)(176 + (224 - 176) * animState.HoverProgress),
                        (int)(176 + (224 - 176) * animState.HoverProgress)
                    );
                }

                // Draw icon
                using var iconFont = new Font("Segoe MDL2 Assets", 14F);
                var iconText = "\uE895"; // Download/Update icon

                using var iconBrush = new SolidBrush(iconColor);
                var iconSize = e.Graphics.MeasureString(iconText, iconFont);

                // Position icon on the left
                var iconX = 20;
                var iconY = (btnUpdate.Height - iconSize.Height) / 2;
                e.Graphics.DrawString(iconText, iconFont, iconBrush, iconX, iconY);

                // Draw text after icon
                using var textFont = new Font("Segoe UI", 9F, FontStyle.Regular);
                var text = "CHECK FOR UPDATES";
                var textSize = e.Graphics.MeasureString(text, textFont);
                var textX = iconX + iconSize.Width + 10;
                var textY = (btnUpdate.Height - textSize.Height) / 2;
                e.Graphics.DrawString(text, textFont, iconBrush, textX, textY);

                // Draw glow around status indicator if update available
                if (hasUpdate && statusIndicator != null)
                {
                    var indicatorBounds = new Rectangle(
                        statusIndicator.Left - 3,
                        statusIndicator.Top - 3,
                        statusIndicator.Width + 6,
                        statusIndicator.Height + 6
                    );

                    // Multi-layer glow
                    for (int i = 3; i > 0; i--)
                    {
                        var glowAlpha = (int)(20 / i * (0.5 + 0.5 * Math.Sin(pulsePhase)));
                        using var glowBrush = new SolidBrush(Color.FromArgb(glowAlpha, 87, 242, 135));
                        var glowRect = new Rectangle(
                            indicatorBounds.X - i * 2,
                            indicatorBounds.Y - i * 2,
                            indicatorBounds.Width + i * 4,
                            indicatorBounds.Height + i * 4
                        );
                        e.Graphics.FillEllipse(glowBrush, glowRect);
                    }
                }
            };
            btnUpdate.Text = ""; // Clear text since we're drawing it manually
            ConfigureHoverAnimation(btnUpdate);

            // Content Panel
            contentPanel.BackColor = Color.FromArgb(28, 28, 28);
            contentPanel.Controls.Add(botsPanel);
            contentPanel.Controls.Add(hubPanel);
            contentPanel.Controls.Add(logsPanel);
            contentPanel.Controls.Add(headerPanel);
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.Location = new Point(260, 0);
            contentPanel.Margin = new Padding(0);
            contentPanel.Name = "contentPanel";
            contentPanel.Size = new Size(1020, 720);
            contentPanel.TabIndex = 1;

            // Header Panel
            headerPanel.BackColor = Color.FromArgb(28, 28, 28);
            headerPanel.Controls.Add(controlButtonsPanel);
            headerPanel.Controls.Add(titleLabel);
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 100;
            headerPanel.Location = new Point(0, 0);
            headerPanel.Name = "headerPanel";
            headerPanel.Size = new Size(1020, 100);
            headerPanel.TabIndex = 3;
            headerPanel.Paint += HeaderPanel_Paint;
            headerPanel.Resize += HeaderPanel_Resize;

            // Title Label
            titleLabel.AutoSize = true;
            titleLabel.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(224, 224, 224);
            titleLabel.Location = new Point(40, 25);
            titleLabel.MaximumSize = new Size(400, 45);
            titleLabel.Name = "titleLabel";
            titleLabel.Size = new Size(250, 45);
            titleLabel.TabIndex = 0;
            titleLabel.Text = "Bot Management";

            // Control Buttons Panel
            controlButtonsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            controlButtonsPanel.AutoSize = true;
            controlButtonsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            controlButtonsPanel.Controls.Add(btnStart);
            controlButtonsPanel.Controls.Add(btnStop);
            controlButtonsPanel.Controls.Add(btnReboot);
            controlButtonsPanel.Controls.Add(btnRefreshMap);
            controlButtonsPanel.FlowDirection = FlowDirection.LeftToRight;
            controlButtonsPanel.Location = new Point(contentPanel.Width - 520, 30);
            controlButtonsPanel.Name = "controlButtonsPanel";
            controlButtonsPanel.Size = new Size(480, 40);
            controlButtonsPanel.TabIndex = 1;
            controlButtonsPanel.BackColor = Color.Transparent;
            controlButtonsPanel.WrapContents = false;

            // Control Buttons
            ConfigureControlButton(btnStart, "START ALL", Color.FromArgb(87, 242, 135)); // #57f287
            ConfigureControlButton(btnStop, "STOP ALL", Color.FromArgb(237, 66, 69)); // #ed4245
            ConfigureControlButton(btnReboot, "REBOOT", Color.FromArgb(88, 101, 242)); // #5865f2
            ConfigureControlButton(btnRefreshMap, "REFRESH MAP", Color.FromArgb(254, 231, 92)); // #fee75c

            btnStart.Click += B_Start_Click;
            btnStop.Click += B_Stop_Click;
            btnReboot.Click += B_RebootAndStop_Click;
            btnRefreshMap.Click += RefreshMap_Click;

            // Bots Panel
            botsPanel.BackColor = Color.Transparent;
            botsPanel.Controls.Add(FLP_Bots);
            botsPanel.Controls.Add(botHeaderPanel);
            botsPanel.Dock = DockStyle.Fill;
            botsPanel.Location = new Point(0, 100);
            botsPanel.Name = "botsPanel";
            botsPanel.Padding = new Padding(40);
            botsPanel.Size = new Size(1020, 620);
            botsPanel.TabIndex = 0;
            botsPanel.Visible = true;

            // Bot Header Panel
            botHeaderPanel.BackColor = Color.FromArgb(35, 35, 35);
            botHeaderPanel.Controls.Add(addBotPanel);
            botHeaderPanel.Dock = DockStyle.Top;
            botHeaderPanel.Height = 100;
            botHeaderPanel.Location = new Point(40, 40);
            botHeaderPanel.Name = "botHeaderPanel";
            botHeaderPanel.Size = new Size(940, 100);
            botHeaderPanel.TabIndex = 1;
            CreateRoundedPanel(botHeaderPanel);

            // Add Bot Panel
            addBotPanel.SuspendLayout();
            addBotPanel.Controls.Add(B_New);
            addBotPanel.Controls.Add(CB_Routine);
            addBotPanel.Controls.Add(CB_Protocol);
            addBotPanel.Controls.Add(NUD_Port);
            addBotPanel.Controls.Add(TB_IP);
            addBotPanel.Dock = DockStyle.Fill;
            addBotPanel.Location = new Point(0, 0);
            addBotPanel.Name = "addBotPanel";
            addBotPanel.Size = new Size(940, 100);
            addBotPanel.TabIndex = 0;
            addBotPanel.BackColor = Color.Transparent;
            EnableDoubleBuffering(addBotPanel);
            addBotPanel.ResumeLayout(false);

            // Bot Configuration Controls
            TB_IP.BackColor = Color.FromArgb(28, 28, 28);
            TB_IP.BorderStyle = BorderStyle.FixedSingle;
            TB_IP.Font = new Font("Segoe UI", 11F);
            TB_IP.ForeColor = Color.FromArgb(224, 224, 224);
            TB_IP.Location = new Point(30, 35);
            TB_IP.Name = "TB_IP";
            TB_IP.PlaceholderText = "IP Address";
            TB_IP.Size = new Size(150, 25);
            TB_IP.TabIndex = 0;
            TB_IP.Text = "192.168.0.1";

            ConfigureNumericUpDown(NUD_Port, 190, 35, 80);
            NUD_Port.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            NUD_Port.Value = new decimal(new int[] { 6000, 0, 0, 0 });

            CB_Protocol.SuspendLayout();
            ConfigureComboBox(CB_Protocol, 280, 35, 120);
            CB_Protocol.SelectedIndexChanged += CB_Protocol_SelectedIndexChanged;
            CB_Protocol.ResumeLayout();

            CB_Routine.SuspendLayout();
            ConfigureComboBox(CB_Routine, 410, 35, 200);
            CB_Routine.ResumeLayout();

            // Add Bot Button with glow effect
            B_New.BackColor = Color.FromArgb(87, 242, 135);
            B_New.FlatAppearance.BorderSize = 0;
            B_New.FlatStyle = FlatStyle.Flat;
            B_New.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            B_New.ForeColor = Color.FromArgb(28, 28, 28);
            B_New.Location = new Point(620, 30);
            B_New.Name = "B_New";
            B_New.Size = new Size(120, 40);
            B_New.TabIndex = 4;
            B_New.Text = "ADD BOT";
            B_New.UseVisualStyleBackColor = false;
            B_New.Click += B_New_Click;
            B_New.Cursor = Cursors.Hand;
            ConfigureGlowButton(B_New);

            // Bot List Panel
            FLP_Bots.AutoScroll = true;
            FLP_Bots.BackColor = Color.Transparent;
            FLP_Bots.Dock = DockStyle.Fill;
            FLP_Bots.FlowDirection = FlowDirection.TopDown;
            FLP_Bots.Location = new Point(40, 140);
            FLP_Bots.Margin = new Padding(0, 20, 0, 0);
            FLP_Bots.Name = "FLP_Bots";
            FLP_Bots.Padding = new Padding(0);
            FLP_Bots.Size = new Size(940, 440);
            FLP_Bots.TabIndex = 0;
            FLP_Bots.WrapContents = false;
            FLP_Bots.Resize += FLP_Bots_Resize;

            // Hub Panel
            hubPanel.BackColor = Color.Transparent;
            hubPanel.Controls.Add(PG_Hub);
            hubPanel.Dock = DockStyle.Fill;
            hubPanel.Location = new Point(0, 100);
            hubPanel.Name = "hubPanel";
            hubPanel.Padding = new Padding(40);
            hubPanel.Size = new Size(1020, 620);
            hubPanel.TabIndex = 1;
            hubPanel.Visible = false;

            // Property Grid Container
            var pgContainer = new Panel();
            pgContainer.BackColor = Color.FromArgb(35, 35, 35);
            pgContainer.Dock = DockStyle.Fill;
            pgContainer.Location = new Point(40, 40);
            pgContainer.Name = "pgContainer";
            pgContainer.Padding = new Padding(2);
            pgContainer.Size = new Size(940, 540);
            CreateRoundedPanel(pgContainer);
            hubPanel.Controls.Add(pgContainer);

            // Property Grid
            PG_Hub.BackColor = Color.FromArgb(35, 35, 35);
            PG_Hub.CategoryForeColor = Color.FromArgb(224, 224, 224);
            PG_Hub.CategorySplitterColor = Color.FromArgb(50, 50, 50);
            PG_Hub.CommandsBackColor = Color.FromArgb(35, 35, 35);
            PG_Hub.CommandsForeColor = Color.FromArgb(224, 224, 224);
            PG_Hub.Dock = DockStyle.Fill;
            PG_Hub.Font = new Font("Segoe UI", 9F);
            PG_Hub.HelpBackColor = Color.FromArgb(35, 35, 35);
            PG_Hub.HelpForeColor = Color.FromArgb(176, 176, 176);
            PG_Hub.LineColor = Color.FromArgb(50, 50, 50);
            PG_Hub.Location = new Point(2, 2);
            PG_Hub.Name = "PG_Hub";
            PG_Hub.PropertySort = PropertySort.Categorized;
            PG_Hub.Size = new Size(936, 536);
            PG_Hub.TabIndex = 0;
            PG_Hub.ToolbarVisible = false;
            PG_Hub.ViewBackColor = Color.FromArgb(28, 28, 28);
            PG_Hub.ViewForeColor = Color.FromArgb(224, 224, 224);
            pgContainer.Controls.Add(PG_Hub);

            // Logs Panel
            logsPanel.BackColor = Color.Transparent;
            logsPanel.Dock = DockStyle.Fill;
            logsPanel.Location = new Point(0, 100);
            logsPanel.Name = "logsPanel";
            logsPanel.Padding = new Padding(40);
            logsPanel.Size = new Size(1020, 620);
            logsPanel.TabIndex = 2;
            logsPanel.Visible = false;

            // Logs Container
            var logsContainer = new Panel();
            logsContainer.BackColor = Color.FromArgb(35, 35, 35);
            logsContainer.Dock = DockStyle.Fill;
            logsContainer.Location = new Point(40, 110);
            logsContainer.Margin = new Padding(0, 20, 0, 0);
            logsContainer.Name = "logsContainer";
            logsContainer.Padding = new Padding(2);
            logsContainer.Size = new Size(940, 470);
            CreateRoundedPanel(logsContainer);
            logsPanel.Controls.Add(logsContainer);
            logsPanel.Controls.Add(logsHeaderPanel);

            // Logs Header Panel
            logsHeaderPanel.BackColor = Color.FromArgb(35, 35, 35);
            logsHeaderPanel.Controls.Add(btnClearLogs);
            logsHeaderPanel.Controls.Add(searchStatusLabel);
            logsHeaderPanel.Controls.Add(logSearchBox);
            logsHeaderPanel.Dock = DockStyle.Top;
            logsHeaderPanel.Height = 60;
            logsHeaderPanel.Location = new Point(40, 40);
            logsHeaderPanel.Name = "logsHeaderPanel";
            logsHeaderPanel.Padding = new Padding(20, 15, 20, 15);
            logsHeaderPanel.Size = new Size(940, 60);
            logsHeaderPanel.TabIndex = 1;
            CreateRoundedPanel(logsHeaderPanel);

            // Log Search Box
            logSearchBox.BackColor = Color.FromArgb(28, 28, 28);
            logSearchBox.BorderStyle = BorderStyle.FixedSingle;
            logSearchBox.Dock = DockStyle.Fill;
            logSearchBox.Font = new Font("Segoe UI", 10F);
            logSearchBox.ForeColor = Color.FromArgb(224, 224, 224);
            logSearchBox.Location = new Point(20, 15);
            logSearchBox.Name = "logSearchBox";
            logSearchBox.PlaceholderText = "Search logs (Enter = next, Shift+Enter = previous)...";
            logSearchBox.Size = new Size(700, 30);
            logSearchBox.TabIndex = 0;
            logSearchBox.TextChanged += LogSearchBox_TextChanged;
            logSearchBox.KeyDown += LogSearchBox_KeyDown;

            // Search Status Label
            searchStatusLabel.AutoSize = false;
            searchStatusLabel.BackColor = Color.Transparent;
            searchStatusLabel.Dock = DockStyle.Right;
            searchStatusLabel.Font = new Font("Segoe UI", 9F);
            searchStatusLabel.ForeColor = Color.FromArgb(176, 176, 176);
            searchStatusLabel.Location = new Point(670, 15);
            searchStatusLabel.Name = "searchStatusLabel";
            searchStatusLabel.Size = new Size(130, 30);
            searchStatusLabel.TabIndex = 2;
            searchStatusLabel.Text = "";
            searchStatusLabel.TextAlign = ContentAlignment.MiddleRight;

            // Clear Logs Button
            btnClearLogs.BackColor = Color.FromArgb(237, 66, 69);
            btnClearLogs.Dock = DockStyle.Right;
            btnClearLogs.FlatAppearance.BorderSize = 0;
            btnClearLogs.FlatStyle = FlatStyle.Flat;
            btnClearLogs.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnClearLogs.ForeColor = Color.White;
            btnClearLogs.Location = new Point(800, 15);
            btnClearLogs.Name = "btnClearLogs";
            btnClearLogs.Size = new Size(120, 30);
            btnClearLogs.TabIndex = 1;
            btnClearLogs.Text = "CLEAR LOGS";
            btnClearLogs.UseVisualStyleBackColor = false;
            btnClearLogs.Cursor = Cursors.Hand;
            btnClearLogs.Click += (s, e) => {
                RTB_Logs.Clear();
                logSearchBox.Clear();
                searchStatusLabel.Text = "";
                currentSearchIndex = -1;
                lastSearchText = "";
            };
            ConfigureGlowButton(btnClearLogs);

            // Rich Text Box
            RTB_Logs.BackColor = Color.FromArgb(28, 28, 28);
            RTB_Logs.BorderStyle = BorderStyle.None;
            RTB_Logs.Dock = DockStyle.Fill;
            RTB_Logs.Font = new Font("Consolas", 10F);
            RTB_Logs.ForeColor = Color.FromArgb(224, 224, 224);
            RTB_Logs.Location = new Point(2, 2);
            RTB_Logs.Name = "RTB_Logs";
            RTB_Logs.ReadOnly = true;
            RTB_Logs.Size = new Size(936, 466);
            RTB_Logs.TabIndex = 0;
            RTB_Logs.Text = "";
            RTB_Logs.TextChanged += RTB_Logs_TextChanged;
            RTB_Logs.KeyDown += RTB_Logs_KeyDown;
            logsContainer.Controls.Add(RTB_Logs);

            Controls.Add(mainLayoutPanel);

            // Configure Tray Icon
            ConfigureTrayIcon();

            mainLayoutPanel.ResumeLayout(false);
            sidebarPanel.ResumeLayout(false);
            navButtonsPanel.ResumeLayout(false);
            sidebarBottomPanel.ResumeLayout(false);
            headerPanel.ResumeLayout(false);
            headerPanel.PerformLayout();
            controlButtonsPanel.ResumeLayout(false);
            contentPanel.ResumeLayout(false);
            botsPanel.ResumeLayout(false);
            botHeaderPanel.ResumeLayout(false);
            addBotPanel.ResumeLayout(false);
            addBotPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)NUD_Port).EndInit();
            hubPanel.ResumeLayout(false);
            logsPanel.ResumeLayout(false);
            logsHeaderPanel.ResumeLayout(false);
            logsHeaderPanel.PerformLayout();
            ResumeLayout(false);

            // Start animation timer
            animationTimer.Start();

            // Enable double buffering on all panels
            EnableDoubleBuffering(mainLayoutPanel);
            EnableDoubleBuffering(sidebarPanel);
            EnableDoubleBuffering(contentPanel);
            EnableDoubleBuffering(headerPanel);
            EnableDoubleBuffering(botsPanel);
            EnableDoubleBuffering(hubPanel);
            EnableDoubleBuffering(logsPanel);
            EnableDoubleBuffering(FLP_Bots);
        }

        private void EnableDoubleBuffering(Control control)
        {
            if (control == null) return;

            typeof(Control).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                null, control, new object[] { true });
        }

        private void ConfigureExitButton()
        {
            btnNavExit.BackColor = Color.FromArgb(18, 18, 18);
            btnNavExit.Cursor = Cursors.Hand;
            btnNavExit.FlatAppearance.BorderSize = 0;
            btnNavExit.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 30, 30);
            btnNavExit.FlatStyle = FlatStyle.Flat;
            btnNavExit.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
            btnNavExit.ForeColor = Color.FromArgb(237, 66, 69); // Red color for exit
            btnNavExit.Location = new Point(0, 220);
            btnNavExit.Margin = new Padding(0, 40, 0, 0); // Extra margin to separate from other buttons
            btnNavExit.Name = "btnNavExit";
            btnNavExit.Padding = new Padding(60, 0, 0, 0);
            btnNavExit.Size = new Size(260, 50);
            btnNavExit.TabIndex = 3;
            btnNavExit.Text = "EXIT";
            btnNavExit.TextAlign = ContentAlignment.MiddleLeft;
            btnNavExit.UseVisualStyleBackColor = false;
            btnNavExit.Tag = new ButtonAnimationState();

            btnNavExit.Paint += (s, e) => {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                var animState = btnNavExit.Tag as ButtonAnimationState;

                // Draw hover background
                if (animState != null && animState.HoverProgress > 0)
                {
                    using var brush = new SolidBrush(Color.FromArgb((int)(30 * animState.HoverProgress), 237, 66, 69));
                    e.Graphics.FillRectangle(brush, btnNavExit.ClientRectangle);
                }

                // Draw icon
                var iconRect = new Rectangle(20, (btnNavExit.Height - 24) / 2, 24, 24);

                // Draw exit icon
                using var iconFont = new Font("Segoe MDL2 Assets", 16F);
                var iconText = "\uE8BB"; // Power/Exit icon

                using var iconBrush = new SolidBrush(btnNavExit.ForeColor);
                var textSize = e.Graphics.MeasureString(iconText, iconFont);
                var textX = iconRect.X + (iconRect.Width - textSize.Width) / 2;
                var textY = iconRect.Y + (iconRect.Height - textSize.Height) / 2;

                e.Graphics.DrawString(iconText, iconFont, iconBrush, textX, textY);
            };

            btnNavExit.Click += (s, e) => ExitApplication();
            ConfigureHoverAnimation(btnNavExit);
        }

        private void ConfigureTrayIcon()
        {
            // Configure tray icon
            trayIcon.Icon = Icon;
            trayIcon.Text = (Config?.Hub?.BotName != null && !string.IsNullOrEmpty(Config.Hub.BotName)) ? Config.Hub.BotName : "S/V RaidBot";
            trayIcon.Visible = false;
            trayIcon.DoubleClick += (s, e) => ShowFromTray();

            // Configure tray context menu
            trayContextMenu.BackColor = Color.FromArgb(35, 35, 35);
            trayContextMenu.Font = new Font("Segoe UI", 10F);
            trayContextMenu.Renderer = new MainMenuRenderer();

            // Add menu items
            var showMenuItem = new ToolStripMenuItem("Show Window")
            {
                ForeColor = Color.FromArgb(224, 224, 224)
            };
            showMenuItem.Click += (s, e) => ShowFromTray();

            var startMenuItem = new ToolStripMenuItem("Start All Bots")
            {
                ForeColor = Color.FromArgb(87, 242, 135)
            };
            startMenuItem.Click += (s, e) => B_Start_Click(s, e);

            var stopMenuItem = new ToolStripMenuItem("Stop All Bots")
            {
                ForeColor = Color.FromArgb(237, 66, 69)
            };
            stopMenuItem.Click += (s, e) => B_Stop_Click(s, e);

            var exitMenuItem = new ToolStripMenuItem("Exit")
            {
                ForeColor = Color.FromArgb(237, 66, 69)
            };
            exitMenuItem.Click += (s, e) => ExitApplication();

            trayContextMenu.Items.AddRange(new ToolStripItem[] {
                showMenuItem,
                new ToolStripSeparator(),
                startMenuItem,
                stopMenuItem,
                new ToolStripSeparator(),
                exitMenuItem
            });

            trayIcon.ContextMenuStrip = trayContextMenu;
        }

        // Custom menu renderer for dark theme
        private class MainMenuRenderer : ToolStripProfessionalRenderer
        {
            public MainMenuRenderer() : base(new MainColorTable()) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var rc = new Rectangle(Point.Empty, e.Item.Size);
                var c = e.Item.Selected ? Color.FromArgb(50, 50, 50) : Color.FromArgb(35, 35, 35);
                using (var brush = new SolidBrush(c))
                    e.Graphics.FillRectangle(brush, rc);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled)
                    e.TextColor = Color.FromArgb(100, 100, 100);
                base.OnRenderItemText(e);
            }
        }

        private class MainColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(50, 50, 50);
            public override Color MenuItemBorder => Color.FromArgb(88, 101, 242);
            public override Color MenuBorder => Color.FromArgb(50, 50, 50);
            public override Color ToolStripDropDownBackground => Color.FromArgb(35, 35, 35);
            public override Color ImageMarginGradientBegin => Color.FromArgb(35, 35, 35);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(35, 35, 35);
            public override Color ImageMarginGradientEnd => Color.FromArgb(35, 35, 35);
            public override Color SeparatorDark => Color.FromArgb(50, 50, 50);
            public override Color SeparatorLight => Color.FromArgb(60, 60, 60);
        }

        private void ConfigureNavButton(Button btn, string text, int index, string tooltip)
        {
            btn.BackColor = Color.FromArgb(18, 18, 18);
            btn.Cursor = Cursors.Hand;
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 30, 30);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
            btn.ForeColor = Color.FromArgb(176, 176, 176); // #B0B0B0
            btn.Location = new Point(0, 40 + (index * 60));
            btn.Margin = new Padding(0, 0, 0, 10);
            btn.Name = $"btnNav{text.Replace(" ", "")}";
            btn.Padding = new Padding(60, 0, 0, 0);
            btn.Size = new Size(260, 50);
            btn.TabIndex = index;
            btn.Text = text;
            btn.TextAlign = ContentAlignment.MiddleLeft;
            btn.UseVisualStyleBackColor = false;
            btn.Tag = new ButtonAnimationState();

            // Add icon space
            btn.Paint += (s, e) => {
                var animState = btn.Tag as ButtonAnimationState;

                // Draw selection indicator
                if (btn.BackColor == Color.FromArgb(30, 30, 30))
                {
                    using var brush = new LinearGradientBrush(
                        new RectangleF(0, 0, 4, btn.Height),
                        Color.FromArgb(87, 242, 135),
                        Color.FromArgb(88, 101, 242),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillRectangle(brush, 0, 0, 4, btn.Height);
                }

                // Draw icon without glow
                var iconRect = new Rectangle(20, (btn.Height - 24) / 2, 24, 24);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Draw font icon
                using var iconFont = new Font("Segoe MDL2 Assets", 16F);
                string iconText = index switch
                {
                    0 => "\uE77B", // Robot icon for Bots
                    1 => "\uE713", // Settings icon for Configuration
                    2 => "\uE7C3", // Document icon for Logs
                    _ => "\uE700"  // Default icon
                };

                // Adjust color based on selection state
                using var iconBrush = new SolidBrush(
                    btn.BackColor == Color.FromArgb(30, 30, 30)
                        ? Color.FromArgb(224, 224, 224)  // Selected - bright
                        : Color.FromArgb(150, 150, 150)  // Unselected - dimmed
                );

                // Center the icon in the rectangle
                var textSize = e.Graphics.MeasureString(iconText, iconFont);
                var textX = iconRect.X + (iconRect.Width - textSize.Width) / 2;
                var textY = iconRect.Y + (iconRect.Height - textSize.Height) / 2;

                e.Graphics.DrawString(iconText, iconFont, iconBrush, textX, textY);
            };

            btn.Click += (s, e) => {
                // Reset all nav buttons
                foreach (Button navBtn in navButtonsPanel.Controls)
                {
                    navBtn.BackColor = Color.FromArgb(18, 18, 18);
                    navBtn.ForeColor = Color.FromArgb(176, 176, 176);
                }

                // Highlight selected with animation
                btn.BackColor = Color.FromArgb(30, 30, 30);
                btn.ForeColor = Color.FromArgb(224, 224, 224);

                // Show appropriate panel with fade transition
                TransitionPanels(index);

                // Update title with fade effect
                titleLabel.Text = index switch
                {
                    0 => "Bot Management",
                    1 => "Configuration",
                    2 => "System Logs",
                    _ => "RaidBot"
                };
            };

            // Configure hover animation
            ConfigureHoverAnimation(btn);

            // Set initial selection
            if (index == 0)
            {
                btn.BackColor = Color.FromArgb(30, 30, 30);
                btn.ForeColor = Color.FromArgb(224, 224, 224);
            }
        }

        private void ConfigureControlButton(Button btn, string text, Color baseColor)
        {
            btn.BackColor = baseColor;
            btn.Cursor = Cursors.Hand;
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btn.ForeColor = Color.FromArgb(28, 28, 28); // Dark text on bright buttons
            btn.Margin = new Padding(5, 0, 5, 0);
            btn.Name = $"btn{text.Replace(" ", "")}";
            btn.Padding = new Padding(5, 0, 5, 0);
            btn.Size = new Size(115, 40);
            btn.TabIndex = 0;
            btn.Text = text;
            btn.UseVisualStyleBackColor = false;
            btn.Tag = new ButtonAnimationState { BaseColor = baseColor };

            // Create rounded corners
            CreateRoundedButton(btn);

            // Configure glow effect
            ConfigureGlowButton(btn);
        }

        private void ConfigureTextBox(TextBox tb, string placeholder, int x, int y, int width)
        {
            tb.BackColor = Color.FromArgb(28, 28, 28);
            tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font("Segoe UI", 11F);
            tb.ForeColor = Color.FromArgb(224, 224, 224);
            tb.Location = new Point(x, y);
            tb.Name = tb.Name;
            tb.PlaceholderText = placeholder;
            tb.Size = new Size(width, 25);
            tb.TabIndex = 0;

            // Focus effects with border color change
            tb.Enter += (s, e) => {
                if (tb.Parent != null)
                    tb.Parent.BackColor = Color.FromArgb(88, 101, 242);
            };
            tb.Leave += (s, e) => {
                if (tb.Parent != null)
                    tb.Parent.BackColor = Color.FromArgb(50, 50, 50);
            };
        }

        private void ConfigureNumericUpDown(NumericUpDown nud, int x, int y, int width)
        {
            nud.BackColor = Color.FromArgb(28, 28, 28);
            nud.BorderStyle = BorderStyle.None;
            nud.Font = new Font("Segoe UI", 11F);
            nud.ForeColor = Color.FromArgb(224, 224, 224);
            nud.Location = new Point(x, y);
            nud.Name = nud.Name;
            nud.Size = new Size(width, 25);
            nud.TabIndex = 1;
        }

        private void ConfigureComboBox(ComboBox cb, int x, int y, int width)
        {
            cb.BackColor = Color.FromArgb(28, 28, 28);
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
            cb.Font = new Font("Segoe UI", 11F);
            cb.ForeColor = Color.FromArgb(224, 224, 224);
            cb.Location = new Point(x, y);
            cb.Name = cb.Name;
            cb.Size = new Size(width, 28);
            cb.TabIndex = 2;
        }

        private void ConfigureHoverAnimation(Control control)
        {
            var animState = control.Tag as ButtonAnimationState ?? new ButtonAnimationState();
            control.Tag = animState;

            control.MouseEnter += (s, e) => {
                animState.IsHovering = true;
                animState.AnimationStart = DateTime.Now;
            };

            control.MouseLeave += (s, e) => {
                animState.IsHovering = false;
                animState.AnimationStart = DateTime.Now;
            };
        }

        private void ConfigureGlowButton(Button btn)
        {
            ConfigureHoverAnimation(btn);

            btn.Paint += (s, e) => {
                var animState = btn.Tag as ButtonAnimationState;
                if (animState != null && animState.HoverProgress > 0)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                    // Multi-layer glow effect
                    var glowAlpha = (int)(60 * animState.HoverProgress);
                    using (var glowBrush = new SolidBrush(Color.FromArgb(glowAlpha, btn.BackColor)))
                    {
                        for (int i = 1; i <= 3; i++)
                        {
                            var rect = new Rectangle(-i * 2, -i * 2, btn.Width + i * 4, btn.Height + i * 4);
                            e.Graphics.FillRectangle(glowBrush, rect);
                        }
                    }
                }
            };
        }

        private void CreateRoundedPanel(Panel panel)
        {
            panel.Paint += (s, e) => {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = new GraphicsPath();
                var rect = panel.ClientRectangle;
                rect.Inflate(-1, -1);
                GraphicsExtensions.AddRoundedRectangle(path, rect, 8);
                panel.Region = new Region(path);
            };
        }

        private void CreateRoundedButton(Button btn)
        {
            using var path = new GraphicsPath();
            GraphicsExtensions.AddRoundedRectangle(path, btn.ClientRectangle, 6);
            btn.Region = new Region(path);
        }

        private void CreateCircularRegion(Control control)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, control.Width, control.Height);
            control.Region = new Region(path);
        }

        private void LogoPanel_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Cache the gradient brush to prevent recreation
            if (_logoBrush == null)
            {
                _logoBrush = new LinearGradientBrush(
                    logoPanel.ClientRectangle,
                    Color.FromArgb(88, 101, 242),
                    Color.FromArgb(87, 242, 135),
                    LinearGradientMode.ForwardDiagonal);
            }

            e.Graphics.FillRectangle(_logoBrush, logoPanel.ClientRectangle);

            // Draw text with glow
            using var font = new Font("Segoe UI", 22F, FontStyle.Bold);
            var text = "S/V RAIDBOT";
            var textSize = e.Graphics.MeasureString(text, font);
            var x = (logoPanel.Width - textSize.Width) / 2;
            var y = (logoPanel.Height - textSize.Height) / 2;

            // Simple glow effect - reduced layers
            using var glowBrush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
            e.Graphics.DrawString(text, font, glowBrush, x - 2, y - 2);

            // Main text
            using var textBrush = new SolidBrush(Color.FromArgb(18, 18, 18));
            e.Graphics.DrawString(text, font, textBrush, x, y);
        }

        private void HeaderPanel_Paint(object sender, PaintEventArgs e)
        {
            // Bottom border with gradient
            using var brush = new LinearGradientBrush(
                new RectangleF(0, headerPanel.Height - 2, headerPanel.Width, 2),
                Color.FromArgb(50, 88, 101, 242),
                Color.FromArgb(50, 87, 242, 135),
                LinearGradientMode.Horizontal);

            e.Graphics.FillRectangle(brush, 0, headerPanel.Height - 2, headerPanel.Width, 2);
        }

        private void HeaderPanel_Resize(object sender, EventArgs e)
        {
            // Reposition control buttons panel to ensure it stays properly aligned
            if (controlButtonsPanel != null && headerPanel != null)
            {
                int rightMargin = 40;
                int minLeftPosition = titleLabel.Right + 50; // Ensure space between title and buttons
                int desiredX = headerPanel.Width - controlButtonsPanel.Width - rightMargin;

                // Ensure buttons don't overlap with title
                controlButtonsPanel.Location = new Point(Math.Max(minLeftPosition, desiredX), 30);
            }
        }

        private void Main_Shown(object sender, EventArgs e)
        {
            // Ensure proper layout when form is first shown
            HeaderPanel_Resize(headerPanel, EventArgs.Empty);

            // Ensure status indicator is properly positioned
            if (statusIndicator != null && btnUpdate != null)
            {
                statusIndicator.Location = new Point(btnUpdate.ClientSize.Width - 25, 20);
            }

            Refresh();
        }

        private int currentSearchIndex = -1;
        private string lastSearchText = "";

        private void LogSearchBox_TextChanged(object sender, EventArgs e)
        {
            // Reset search when text changes
            currentSearchIndex = -1;
            lastSearchText = logSearchBox.Text;
            searchStatusLabel.Text = "";

            // Clear any existing highlights
            if (RTB_Logs.Text.Length > 0)
            {
                RTB_Logs.SelectAll();
                RTB_Logs.SelectionBackColor = RTB_Logs.BackColor;
                RTB_Logs.SelectionColor = RTB_Logs.ForeColor;
                RTB_Logs.Select(0, 0);
            }

            // Perform initial search if text is not empty
            if (!string.IsNullOrWhiteSpace(logSearchBox.Text))
            {
                HighlightAllMatches();

                // Update match count
                string searchText = logSearchBox.Text.ToLower();
                string content = RTB_Logs.Text.ToLower();
                int matchCount = CountMatches(searchText, content);

                if (matchCount > 0)
                {
                    searchStatusLabel.ForeColor = Color.FromArgb(87, 242, 135);
                    searchStatusLabel.Text = $"{matchCount} matches";
                }
                else
                {
                    searchStatusLabel.ForeColor = Color.FromArgb(237, 66, 69);
                    searchStatusLabel.Text = "No matches";
                }
            }
        }

        private void LogSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true; // Prevent the ding sound
                if (e.Shift)
                    FindPrevious();
                else
                    FindNext();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                // Clear search
                logSearchBox.Clear();
                searchStatusLabel.Text = "";
                RTB_Logs.Focus();
            }
            else if (e.KeyCode == Keys.F3 || (e.Control && e.KeyCode == Keys.F))
            {
                // Allow F3 or Ctrl+F to focus search box
                e.SuppressKeyPress = true;
                logSearchBox.Focus();
                logSearchBox.SelectAll();
            }
        }

        private void HighlightAllMatches()
        {
            if (string.IsNullOrWhiteSpace(logSearchBox.Text) || RTB_Logs.Text.Length == 0)
                return;

            string searchText = logSearchBox.Text.ToLower();
            string content = RTB_Logs.Text.ToLower();

            RTB_Logs.SuspendLayout();

            int index = 0;
            while (index < content.Length)
            {
                index = content.IndexOf(searchText, index);
                if (index == -1)
                    break;

                RTB_Logs.Select(index, searchText.Length);
                RTB_Logs.SelectionBackColor = Color.FromArgb(88, 101, 242); // Blue highlight
                RTB_Logs.SelectionColor = Color.White;

                index += searchText.Length;
            }

            RTB_Logs.Select(0, 0);
            RTB_Logs.ResumeLayout();
        }

        private void FindNext()
        {
            if (string.IsNullOrWhiteSpace(logSearchBox.Text) || RTB_Logs.Text.Length == 0)
                return;

            string searchText = logSearchBox.Text.ToLower();
            string content = RTB_Logs.Text.ToLower();

            // Start searching from current position
            int startIndex = currentSearchIndex + 1;
            if (startIndex >= content.Length)
                startIndex = 0;

            int index = content.IndexOf(searchText, startIndex);

            // If not found from current position, try from beginning
            if (index == -1 && startIndex > 0)
            {
                index = content.IndexOf(searchText, 0);
            }

            if (index != -1)
            {
                // First restore previous highlight to blue
                if (currentSearchIndex >= 0 && currentSearchIndex < content.Length - searchText.Length + 1)
                {
                    RTB_Logs.Select(currentSearchIndex, searchText.Length);
                    RTB_Logs.SelectionBackColor = Color.FromArgb(88, 101, 242);
                    RTB_Logs.SelectionColor = Color.White;
                }

                // Highlight current match with different color
                RTB_Logs.Select(index, searchText.Length);
                RTB_Logs.SelectionBackColor = Color.FromArgb(87, 242, 135); // Green for current
                RTB_Logs.SelectionColor = Color.Black;
                RTB_Logs.ScrollToCaret();

                currentSearchIndex = index;

                // Update search status
                int matchCount = CountMatches(searchText, content);
                int currentMatch = CountMatchesBefore(searchText, content, index) + 1;
                searchStatusLabel.ForeColor = Color.FromArgb(87, 242, 135);
                searchStatusLabel.Text = $"{currentMatch} of {matchCount}";
            }
            else
            {
                // No matches found
                searchStatusLabel.ForeColor = Color.FromArgb(237, 66, 69);
                searchStatusLabel.Text = "No matches";
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private void FindPrevious()
        {
            if (string.IsNullOrWhiteSpace(logSearchBox.Text) || RTB_Logs.Text.Length == 0)
                return;

            string searchText = logSearchBox.Text.ToLower();
            string content = RTB_Logs.Text.ToLower();

            // Start searching from current position backwards
            int startIndex = currentSearchIndex - 1;
            if (startIndex < 0 || currentSearchIndex == -1)
                startIndex = content.Length - searchText.Length;

            // Ensure we don't go beyond the content length
            if (startIndex >= content.Length)
                startIndex = content.Length - 1;

            int index = -1;
            if (startIndex >= 0 && startIndex < content.Length)
            {
                // Search backwards from current position
                string substring = content.Substring(0, Math.Min(startIndex + searchText.Length, content.Length));
                index = substring.LastIndexOf(searchText);
            }

            // If not found from current position, wrap around to end
            if (index == -1)
            {
                index = content.LastIndexOf(searchText);
            }

            if (index != -1)
            {
                // First restore previous highlight to blue
                if (currentSearchIndex >= 0 && currentSearchIndex < content.Length - searchText.Length + 1)
                {
                    RTB_Logs.Select(currentSearchIndex, searchText.Length);
                    RTB_Logs.SelectionBackColor = Color.FromArgb(88, 101, 242);
                    RTB_Logs.SelectionColor = Color.White;
                }

                // Highlight current match with different color
                RTB_Logs.Select(index, searchText.Length);
                RTB_Logs.SelectionBackColor = Color.FromArgb(87, 242, 135); // Green for current
                RTB_Logs.SelectionColor = Color.Black;
                RTB_Logs.ScrollToCaret();

                currentSearchIndex = index;

                // Update search status
                int matchCount = CountMatches(searchText, content);
                int currentMatch = CountMatchesBefore(searchText, content, index) + 1;
                searchStatusLabel.ForeColor = Color.FromArgb(87, 242, 135);
                searchStatusLabel.Text = $"{currentMatch} of {matchCount}";
            }
            else
            {
                // No matches found
                searchStatusLabel.ForeColor = Color.FromArgb(237, 66, 69);
                searchStatusLabel.Text = "No matches";
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private void RTB_Logs_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                e.SuppressKeyPress = true;
                logSearchBox.Focus();
                logSearchBox.SelectAll();
            }
            else if (e.KeyCode == Keys.F3)
            {
                e.SuppressKeyPress = true;
                if (!string.IsNullOrWhiteSpace(logSearchBox.Text))
                {
                    if (e.Shift)
                        FindPrevious();
                    else
                        FindNext();
                }
                else
                {
                    logSearchBox.Focus();
                    logSearchBox.SelectAll();
                }
            }
        }

        private int CountMatches(string searchText, string content)
        {
            int count = 0;
            int index = 0;
            while ((index = content.IndexOf(searchText, index)) != -1)
            {
                count++;
                index += searchText.Length;
            }
            return count;
        }

        private int CountMatchesBefore(string searchText, string content, int beforeIndex)
        {
            int count = 0;
            int index = 0;
            while ((index = content.IndexOf(searchText, index)) != -1 && index < beforeIndex)
            {
                count++;
                index += searchText.Length;
            }
            return count;
        }

        private void UpdateStatusIndicatorPulse()
        {
            // Throttle updates to reduce flickering
            var now = DateTime.Now;
            if ((now - lastIndicatorUpdate).TotalMilliseconds < PULSE_UPDATE_INTERVAL_MS)
                return;

            lastIndicatorUpdate = now;

            // Increment phase for smooth animation
            pulsePhase += 0.1; // Adjusted for new update interval
            if (pulsePhase > Math.PI * 2)
                pulsePhase -= Math.PI * 2;

            Color newColor;

            if (hasUpdate)
            {
                // Calculate pulse using smooth sine wave
                double pulse = (Math.Sin(pulsePhase) + 1) / 2; // Normalized to 0-1

                // Green pulsing when update available
                int minAlpha = 150;
                int maxAlpha = 255;
                int alpha = (int)(minAlpha + (maxAlpha - minAlpha) * pulse);

                newColor = Color.FromArgb(alpha, 87, 242, 135);
            }
            else
            {
                // Dim gray when no update - no pulsing
                newColor = Color.FromArgb(100, 100, 100);
            }

            // Only update and invalidate if color actually changed
            if (newColor != lastIndicatorColor)
            {
                lastIndicatorColor = newColor;
                statusIndicator.BackColor = newColor;
                statusIndicator.Invalidate();

                // Only invalidate button glow area when update is available and color changed
                if (hasUpdate && btnUpdate != null)
                {
                    btnUpdate.Invalidate(new Rectangle(
                        statusIndicator.Left - 10,
                        statusIndicator.Top - 10,
                        statusIndicator.Width + 20,
                        statusIndicator.Height + 20
                    ));
                }
            }
        }

        // Also update the AnimationTimer_Tick method to be more efficient:
        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            var controlsToUpdate = new List<Control>();

            // Update hover animations only for controls that need it
            foreach (Control control in GetAllControls(this))
            {
                if (control.Tag is ButtonAnimationState animState)
                {
                    var elapsed = (DateTime.Now - animState.AnimationStart).TotalMilliseconds;
                    var duration = 150.0; // 150ms animation

                    var oldProgress = animState.HoverProgress;

                    if (animState.IsHovering)
                    {
                        animState.HoverProgress = Math.Min(1.0, elapsed / duration);
                    }
                    else
                    {
                        animState.HoverProgress = Math.Max(0.0, 1.0 - (elapsed / duration));
                    }

                    // Only add to update list if progress actually changed significantly
                    // and animation is in progress (not at 0 or 1)
                    if (Math.Abs(animState.HoverProgress - oldProgress) > 0.01 &&
                        animState.HoverProgress > 0.001 && animState.HoverProgress < 0.999)
                    {
                        controlsToUpdate.Add(control);
                    }
                }
            }

            // Batch invalidate controls
            foreach (var control in controlsToUpdate)
            {
                control.Invalidate();
            }

            // Update status indicator pulse with throttling
            UpdateStatusIndicatorPulse();
        }

        private void TransitionPanels(int index)
        {
            // Hide all panels
            botsPanel.Visible = false;
            hubPanel.Visible = false;
            logsPanel.Visible = false;

            // Force refresh of content panel
            contentPanel.Refresh();

            // Show the selected panel
            switch (index)
            {
                case 0:
                    botsPanel.Visible = true;
                    break;
                case 1:
                    hubPanel.Visible = true;
                    break;
                case 2:
                    logsPanel.Visible = true;
                    logsPanel.Refresh(); // Extra refresh for logs panel
                    break;
            }
        }

        private IEnumerable<Control> GetAllControls(Control container)
        {
            var controls = container.Controls.Cast<Control>();
            return controls.SelectMany(ctrl => GetAllControls(ctrl)).Concat(controls);
        }

        // Animation state class
        private class ButtonAnimationState
        {
            public bool IsHovering { get; set; }
            public DateTime AnimationStart { get; set; }
            public double HoverProgress { get; set; }
            public Color BaseColor { get; set; }
        }

        #endregion

        // Controls
        private TableLayoutPanel mainLayoutPanel;
        private Panel sidebarPanel;
        private Panel contentPanel;
        private Panel headerPanel;
        private Panel logoPanel;
        private FlowLayoutPanel navButtonsPanel;
        private Button btnNavBots;
        private Button btnNavHub;
        private Button btnNavLogs;
        private Button btnNavExit;
        private Panel sidebarBottomPanel;
        private Button btnUpdate;
        private Label titleLabel;
        private FlowLayoutPanel controlButtonsPanel;
        private Button btnStart;
        private Button btnStop;
        private Button btnReboot;
        private Button btnRefreshMap;
        private Panel botsPanel;
        private Panel hubPanel;
        private Panel logsPanel;
        private Panel botHeaderPanel;
        private Panel addBotPanel;
        private TextBox TB_IP;
        private NumericUpDown NUD_Port;
        private ComboBox CB_Protocol;
        private ComboBox CB_Routine;
        private Button B_New;
        private FlowLayoutPanel FLP_Bots;
        private PropertyGrid PG_Hub;
        private RichTextBox RTB_Logs;
        private Panel logsHeaderPanel;
        private TextBox logSearchBox;
        private Label searchStatusLabel;
        private Button btnClearLogs;
        private Panel statusIndicator;
        private System.Windows.Forms.Timer animationTimer;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayContextMenu;
        private bool isExiting = false;
        private LinearGradientBrush _logoBrush;
    }
}