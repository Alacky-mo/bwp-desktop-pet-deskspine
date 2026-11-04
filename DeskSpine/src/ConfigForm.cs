using System.Diagnostics;

namespace DeskSpine
{
    public partial class ConfigForm : Form
    {
        /// <summary>
        /// 版本下拉框映射表
        /// </summary>
        protected static Dictionary<string, Spine.SpineVersion> comboBox_SpineVersion_KV = new()
        {
            { "3.6.x", Spine.SpineVersion.V36 },
            { "3.8.x", Spine.SpineVersion.V38 },
        };

        /// <summary>
        /// 交互方案下拉框映射表
        /// </summary>
        protected static Dictionary<string, TinyEngine.AnimatorType> comboBox_WindowType_KV = new()
        {
            { "碧蓝航线_后宅小人", TinyEngine.AnimatorType.AzurLaneSD },
            { "碧蓝航线_动态立绘", TinyEngine.AnimatorType.AzurLaneDynamic },
            { "明日方舟_动态立绘", TinyEngine.AnimatorType.ArknightsDynamic },
            { "明日方舟_基建小人", TinyEngine.AnimatorType.ArknightsBuild },
            { "明日方舟_战斗小人", TinyEngine.AnimatorType.ArknightsBattle },
            { "阴阳师百闻牌_式神", TinyEngine.AnimatorType.BwpHero },
        };

        private ShellNotifyIcon shellNotifyIcon;
        private ToolStripMenuItem commandPets = new("切换角色");
        private ToolStripMenuItem commandFocus = new("番茄钟");
        private ToolStripMenuItem commandWeather = new("查看天气");
        private ToolStripMenuItem commandDialog = new("台词");
        private System.Timers.Timer hourlyChimeTimer = new(100) { AutoReset = true };
        private System.Timers.Timer focusTimer = new(1000) { AutoReset = true };
        private int focusSeen = 0;
        private bool hasChimed = false;

        /// <summary>
        /// 打开设置窗时看到的「切换角色」槽位。这窗上没有它的控件, 全靠原样带回,
        /// 否则 Value 的 getter 拿 new Config() 的默认值 -1 顶掉它 = 图层模式(全部槽位叠着显示)
        /// </summary>
        private int keptActiveSlot = -1;

        /// <summary>
        /// 台词气泡那段也是原样带回: 这窗上没有它的控件(开关在托盘「台词」里), 而 Value 的 getter
        /// 是 new Config() 现攒的 —— 不回灌就是「用户在托盘上关了开关, 点一次应用又活了」
        /// </summary>
        private DialogConfig keptDialog = new();

        /// <summary>
        /// 定位缓存那段照 keptDialog 同一句话办：这窗上没有它的控件（它不是设置，是缓存，开关在「查看天气」小窗自己身上），
        /// 而 getter 同样是 new Config() 现攒的 —— 不回灌就是「天气刚认出城来，点一次应用把城名抹了」
        /// </summary>
        private WeatherConfig keptWeather = new();

        public ConfigForm()
        {
            InitializeComponent();
            _ = Handle; // 强制创建窗口
            shellNotifyIcon = new(notifyIcon);
            var resources = new System.ComponentModel.ComponentResourceManager(typeof(ConfigForm));
            notifyIcon.Icon = AppIcon.Resolve((Icon?)(SystemValue.SystemUseLightTheme ? resources.GetObject("$this.Icon") : resources.GetObject("notifyIcon.Icon")));
            Icon = AppIcon.Resolve((Icon?)resources.GetObject("$this.Icon"));
            hourlyChimeTimer.Elapsed += HourlyChimeTimer_Elapsed;
            focusTimer.Elapsed += FocusTimer_Elapsed;
            focusTimer.Enabled = true;

            // 托盘菜单在窗体 Show 之前就要能用, 而 Load 只在第一次显示时触发, 所以插菜单的活放在构造里
            contextMenuStrip.Items.Insert(contextMenuStrip.Items.IndexOf(commandConfig), commandPets);
            contextMenuStrip.Items.Insert(contextMenuStrip.Items.IndexOf(commandConfig), commandFocus);
            // 托盘上也给天气留一行：右键那条路以后要先挑一次菜单, 想要一步到位就看这里
            commandWeather.Click += (object? s, EventArgs e) => Program.Weather.OpenAtPet();
            contextMenuStrip.Items.Insert(contextMenuStrip.Items.IndexOf(commandConfig), commandWeather);
            contextMenuStrip.Items.Insert(contextMenuStrip.Items.IndexOf(commandConfig), commandDialog);
        }

        /// <summary>
        /// 气泡图标
        /// </summary>
        public string? BalloonIconPath
        {
            get => balloonIconPath;
            set
            {
                value = string.IsNullOrEmpty(value) ? null : value;
                if (value == balloonIconPath) return;
                if (value is null)
                {
                    balloonIconPath = null;
                    balloonIcon = null;
                }
                else
                {
                    Bitmap newIcon = null;
                    try
                    {
                        newIcon = new Bitmap(value);
                        balloonIcon?.Dispose();
                        balloonIconPath = value;
                        balloonIcon = newIcon;
                    }
                    catch (Exception ex)
                    {
                        // 出厂默认那张按裁定不入库，干净克隆上「文件不在」不是错误；而这里一弹窗就会把启动卡在
                        // InitFromConfig 里 —— 宠物窗口建好了却永远显示不出来。只有用户对着设置窗挑的坏图才出声。
                        if (Visible && File.Exists(value))
                            MessageBox.Show(this, $"{value} 加载失败\n\n{ex}", "气泡图标资源加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private string? balloonIconPath;
        private Bitmap? balloonIcon;

        /// <summary>
        /// 番茄钟自己的气泡图标。和整点报时那颗分开 —— 一个是"该休息了", 一个是"到点了"。
        /// 这里读失败不弹窗也不报错: 退回共用那颗就行, 别因为一张图把提醒弄丢
        /// </summary>
        public string? FocusBalloonIconPath
        {
            get => focusBalloonIconPath;
            set
            {
                value = string.IsNullOrEmpty(value) ? null : value;
                if (value == focusBalloonIconPath) return;
                focusBalloonIconPath = value;
                focusBalloonIcon?.Dispose();
                focusBalloonIcon = null;
                if (value is null) return;
                try { focusBalloonIcon = new Bitmap(value); }
                catch { focusBalloonIcon = null; }
            }
        }
        private string? focusBalloonIconPath;
        private Bitmap? focusBalloonIcon;

        /// <summary>
        /// 获取和填充设置项
        /// </summary>
        public Config Value
        {
            get
            {
                var v = new Config();

                // 系统设置
                v.SystemConfig.AutuRun = checkBox_AutoRun.Checked;
                v.SystemConfig.Visible = checkBox_Visible.Checked;
                v.SystemConfig.BalloonIconPath = string.IsNullOrEmpty(textBox_BalloonIconPath.Text) ? null : textBox_BalloonIconPath.Text;
                v.SystemConfig.HourlyChime = checkBox_HourlyChime.Checked;

                // 基础设置
                v.BasicConfig.WallpaperMode = checkBox_WallpaperMode.Checked;
                v.BasicConfig.MouseClickThrough = checkBox_MouseClickThrough.Checked;
                v.BasicConfig.PositionX = numericUpDown_PositionX.Enabled ? (int)numericUpDown_PositionX.Value : Program.SpineWindow.Position.X;
                v.BasicConfig.PositionY = numericUpDown_PositionY.Enabled ? (int)numericUpDown_PositionY.Value : Program.SpineWindow.Position.Y;
                v.BasicConfig.SizeX = numericUpDown_SizeX.Enabled ? (uint)numericUpDown_SizeX.Value : Program.SpineWindow.Size.X;
                v.BasicConfig.SizeY = numericUpDown_SizeY.Enabled ? (uint)numericUpDown_SizeY.Value : Program.SpineWindow.Size.Y;
                v.BasicConfig.SpinePositionX = numericUpDown_SpinePositionX.Enabled ? (float)numericUpDown_SpinePositionX.Value : Program.SpineWindow.Spine.Position.X;
                v.BasicConfig.SpinePositionY = numericUpDown_SpinePositionY.Enabled ? (float)numericUpDown_SpinePositionY.Value : Program.SpineWindow.Spine.Position.Y;
                v.BasicConfig.SpineFlip = checkBox_SpineFlip.Checked;
                v.BasicConfig.SpineScale = trackBar_SpineScale.Value / 100.0f;
                v.BasicConfig.Opacity = (byte)trackBar_Opacity.Value;
                v.BasicConfig.MaxFps = (uint)trackBar_MaxFps.Value;
                v.BasicConfig.BackgroundColor = new((byte)numericUpDown_BackgroundColorR.Value,
                                                    (byte)numericUpDown_BackgroundColorG.Value,
                                                    (byte)numericUpDown_BackgroundColorB.Value,
                                                    0);
                v.BasicConfig.SpineUsePMA = checkBox_SpineUsePMA.Checked;

                // Spine 设置
                v.SpineConfig.SpineVersion = (Spine.SpineVersion)comboBox_SpineVersion.SelectedValue;
                v.SpineConfig.InteractMode = (TinyEngine.AnimatorType)comboBox_WindowType.SelectedValue;
                v.SpineConfig.ActiveSlot = keptActiveSlot;

                // 设置 Spine 路径
                v.SpineConfig.SkelPath0 = string.IsNullOrEmpty(textBox_SkelPath0.Text) ? null : textBox_SkelPath0.Text;
                v.SpineConfig.SkelPath1 = string.IsNullOrEmpty(textBox_SkelPath1.Text) ? null : textBox_SkelPath1.Text;
                v.SpineConfig.SkelPath2 = string.IsNullOrEmpty(textBox_SkelPath2.Text) ? null : textBox_SkelPath2.Text;
                v.SpineConfig.SkelPath3 = string.IsNullOrEmpty(textBox_SkelPath3.Text) ? null : textBox_SkelPath3.Text;
                v.SpineConfig.SkelPath4 = string.IsNullOrEmpty(textBox_SkelPath4.Text) ? null : textBox_SkelPath4.Text;
                v.SpineConfig.SkelPath5 = string.IsNullOrEmpty(textBox_SkelPath5.Text) ? null : textBox_SkelPath5.Text;
                v.SpineConfig.SkelPath6 = string.IsNullOrEmpty(textBox_SkelPath6.Text) ? null : textBox_SkelPath6.Text;
                v.SpineConfig.SkelPath7 = string.IsNullOrEmpty(textBox_SkelPath7.Text) ? null : textBox_SkelPath7.Text;
                v.SpineConfig.SkelPath8 = string.IsNullOrEmpty(textBox_SkelPath8.Text) ? null : textBox_SkelPath8.Text;
                v.SpineConfig.SkelPath9 = string.IsNullOrEmpty(textBox_SkelPath9.Text) ? null : textBox_SkelPath9.Text;

                // 番茄钟设置
                v.PomodoroConfig.WorkMinutes = (int)numericUpDown_FocusWork.Value;
                v.PomodoroConfig.BreakMinutes = (int)numericUpDown_FocusBreak.Value;
                v.PomodoroConfig.LongBreakMinutes = (int)numericUpDown_FocusLongBreak.Value;
                v.PomodoroConfig.CyclesPerLongBreak = (int)numericUpDown_FocusCycles.Value;
                v.PomodoroConfig.BlockSleepWhileWorking = checkBox_FocusBlockSleep.Checked;
                v.PomodoroConfig.BalloonOnPhaseChange = checkBox_FocusBalloon.Checked;
                v.PomodoroConfig.BalloonIconPath = FocusBalloonIconPath;
                v.PomodoroConfig.AutoStartBreak = checkBox_FocusAutoBreak.Checked;
                v.PomodoroConfig.AutoStartWork = checkBox_FocusAutoWork.Checked;

                // 台词气泡设置: 整段靠 keptDialog 带回来, 一个键都不在这儿现攒
                v.DialogConfig = keptDialog;

                // 定位缓存整段靠 keptWeather 带回来，desk 第 16 条量的就是这一段也能原样回来
                v.WeatherConfig = keptWeather;

                return v;
            }

            set
            {
                // 系统设置
                checkBox_AutoRun.Checked = value.SystemConfig.AutuRun;
                checkBox_Visible.Checked = value.SystemConfig.Visible;
                textBox_BalloonIconPath.Text = value.SystemConfig.BalloonIconPath;
                checkBox_HourlyChime.Checked = value.SystemConfig.HourlyChime;

                // 基础设置
                checkBox_WallpaperMode.Checked = value.BasicConfig.WallpaperMode;
                checkBox_MouseClickThrough.Checked = value.BasicConfig.MouseClickThrough;
                numericUpDown_PositionX.Value = value.BasicConfig.PositionX;
                numericUpDown_PositionY.Value = value.BasicConfig.PositionY;
                numericUpDown_SizeX.Value = value.BasicConfig.SizeX;
                numericUpDown_SizeY.Value = value.BasicConfig.SizeY;
                numericUpDown_SpinePositionX.Value = (decimal)value.BasicConfig.SpinePositionX;
                numericUpDown_SpinePositionY.Value = (decimal)value.BasicConfig.SpinePositionY;
                checkBox_SpineFlip.Checked = value.BasicConfig.SpineFlip;
                trackBar_SpineScale.Value = (int)(value.BasicConfig.SpineScale * 100);
                trackBar_Opacity.Value = value.BasicConfig.Opacity;
                trackBar_MaxFps.Value = (int)value.BasicConfig.MaxFps;
                numericUpDown_BackgroundColorR.Value = value.BasicConfig.BackgroundColor.R;
                numericUpDown_BackgroundColorG.Value = value.BasicConfig.BackgroundColor.G;
                numericUpDown_BackgroundColorB.Value = value.BasicConfig.BackgroundColor.B;
                checkBox_SpineUsePMA.Checked = value.BasicConfig.SpineUsePMA;

                // Spine 设置
                comboBox_SpineVersion.SelectedValue = value.SpineConfig.SpineVersion;
                comboBox_WindowType.SelectedValue = value.SpineConfig.InteractMode;
                keptActiveSlot = value.SpineConfig.ActiveSlot;
                keptDialog = value.DialogConfig;
                keptWeather = value.WeatherConfig;
                textBox_SkelPath0.Text = value.SpineConfig.SkelPath0;
                textBox_SkelPath1.Text = value.SpineConfig.SkelPath1;
                textBox_SkelPath2.Text = value.SpineConfig.SkelPath2;
                textBox_SkelPath3.Text = value.SpineConfig.SkelPath3;
                textBox_SkelPath4.Text = value.SpineConfig.SkelPath4;
                textBox_SkelPath5.Text = value.SpineConfig.SkelPath5;
                textBox_SkelPath6.Text = value.SpineConfig.SkelPath6;
                textBox_SkelPath7.Text = value.SpineConfig.SkelPath7;
                textBox_SkelPath8.Text = value.SpineConfig.SkelPath8;
                textBox_SkelPath9.Text = value.SpineConfig.SkelPath9;

                // 番茄钟设置: 配置文件改得出超范围的数, NumericUpDown 越界是直接抛异常, 所以先夹再上控件
                static decimal Fit(NumericUpDown box, int minutes) => Math.Clamp(minutes, (int)box.Minimum, (int)box.Maximum);
                numericUpDown_FocusWork.Value = Fit(numericUpDown_FocusWork, value.PomodoroConfig.WorkMinutes);
                numericUpDown_FocusBreak.Value = Fit(numericUpDown_FocusBreak, value.PomodoroConfig.BreakMinutes);
                numericUpDown_FocusLongBreak.Value = Fit(numericUpDown_FocusLongBreak, value.PomodoroConfig.LongBreakMinutes);
                numericUpDown_FocusCycles.Value = Fit(numericUpDown_FocusCycles, value.PomodoroConfig.CyclesPerLongBreak);
                checkBox_FocusBlockSleep.Checked = value.PomodoroConfig.BlockSleepWhileWorking;
                checkBox_FocusBalloon.Checked = value.PomodoroConfig.BalloonOnPhaseChange;
                FocusBalloonIconPath = value.PomodoroConfig.BalloonIconPath;
                checkBox_FocusAutoBreak.Checked = value.PomodoroConfig.AutoStartBreak;
                checkBox_FocusAutoWork.Checked = value.PomodoroConfig.AutoStartWork;
            }
        }

        /// <summary>
        /// 整点报时功能
        /// </summary>
        public bool HourlyChime { get => hourlyChimeTimer.Enabled; set => hourlyChimeTimer.Enabled = value; }

        /// <summary>
        /// 番茄钟换阶段弹不弹气泡。计时器一直在数阶段变化, 关了只是不弹 ——
        /// 不然重新打开时会补弹一个早就过期的
        /// </summary>
        public bool FocusBalloon { get; set; } = true;

        /// <summary>
        /// 使用设置项弹出气泡消息
        /// </summary>
        public void ShowBalloonTip(string title, string info) => ShowBalloonTip(title, info, balloonIcon);

        /// <summary>
        /// 用指定那张图弹气泡, 没给就退回设置里那颗
        /// </summary>
        public void ShowBalloonTip(string title, string info, Bitmap? icon)
        {
            if (icon is null) { ShowBalloonTip(title, info, ToolTipIcon.None); }
            else { ShowBalloonTip(title, info, icon.GetHicon()); }
        }

        /// <summary>
        /// 使用指定图标弹出气泡消息
        /// </summary>
        public void ShowBalloonTip(string title, string info, IntPtr balloonIcon)
        {
            title ??= ""; info ??= "";
            if (title.Length <= 0 && info.Length <= 0) info = "~";
            shellNotifyIcon.ShowBalloonTip(title, info, balloonIcon);
        }

        /// <summary>
        /// 使用系统图标弹出气泡消息
        /// </summary>
        public void ShowBalloonTip(string title, string info, ToolTipIcon balloonIcon)
        {
            title ??= ""; info ??= "";
            if (title.Length <= 0 && info.Length <= 0) info = "~";
            notifyIcon.ShowBalloonTip(5, title, info, balloonIcon);
        }

        #region 窗体事件

        private void ConfigForm_Load(object sender, EventArgs e)
        {
            comboBox_SpineVersion.DataSource = new BindingSource(comboBox_SpineVersion_KV, null);
            comboBox_SpineVersion.DisplayMember = "Key";
            comboBox_SpineVersion.ValueMember = "Value";
            comboBox_SpineVersion.SelectedValue = Spine.SpineVersion.V38;

            comboBox_WindowType.DataSource = new BindingSource(comboBox_WindowType_KV, null);
            comboBox_WindowType.DisplayMember = "Key";
            comboBox_WindowType.ValueMember = "Value";
            comboBox_WindowType.SelectedValue = TinyEngine.AnimatorType.AzurLaneSD;
        }

        private void ConfigForm_VisibleChanged(object sender, EventArgs e)
        {
            if (this.Visible)
            {
                Value = Program.CurrentConfig;

                // 恢复默认显示
                Size = new(1600, 900);
                CenterToScreen();
                tabControl_Config.SelectTab(0);

                // 默认锁定位置大小调整
                numericUpDown_PositionX.Enabled = false;
                numericUpDown_PositionY.Enabled = false;
                numericUpDown_SizeX.Enabled = false;
                numericUpDown_SizeY.Enabled = false;
                numericUpDown_SpinePositionX.Enabled = false;
                numericUpDown_SpinePositionY.Enabled = false;
            }
        }

        private void ConfigForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void button_OpenDataFolder_Click(object sender, EventArgs e)
        {
            try 
            { 
                Process.Start("explorer.exe", Program.ProgramDataDirectory); 
            }
            catch (Exception ex) 
            {
                MessageBox.Show(this, $"无法打开文件夹: {ex.Message}", Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error); 
            }
        }

        private void button_Ok_Click(object sender, EventArgs e)
        {
            button_Apply_Click(sender, e);
            this.Hide();
        }

        private void button_Apply_Click(object sender, EventArgs e)
        {
            Program.CurrentConfig = Value;
            Value = Program.CurrentConfig; // 刷新页面值
        }

        private void HourlyChimeTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            DateTime now = DateTime.Now;
            if (now.Minute == 0)
            {
                if (!hasChimed)
                {
                    hasChimed = true;
                    ShowBalloonTip($"北京时间 {now.Hour:d2}: {now.Minute:d2}", "Take a break~");
                    Program.SpineWindow.Spine.HourlyChime();
                }
            }
            else
            {
                hasChimed = false;
            }
        }

        #endregion

        #region 通知栏图标事件

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var wndSize = Program.PerfMonitorForm.Size;
                var iconRect = shellNotifyIcon.Rectangle;
                var halfDeltaWidth = (wndSize.Width - iconRect.Width) / 2;
                var halfDeltaHeight = (wndSize.Height - iconRect.Height) / 2;
                Point location = SystemValue.TaskbarDirection switch
                {
                    EdgeDirection.Left => new(iconRect.Right, iconRect.Top - halfDeltaHeight),
                    EdgeDirection.Top => new(iconRect.Left - halfDeltaWidth, iconRect.Bottom),
                    EdgeDirection.Right => new(iconRect.Left - wndSize.Width, iconRect.Top - halfDeltaHeight),
                    EdgeDirection.Bottom => new(iconRect.Left - halfDeltaWidth, iconRect.Top - wndSize.Height),
                    _ => throw new NotImplementedException()
                };
                Program.PerfMonitorForm.Popup(location);
            }
        }

        private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                commandConfig_Click(sender, EventArgs.Empty);
            }
        }

        #endregion

        #region 菜单命令事件

        private void contextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var currentConfig = Program.CurrentConfig;
            commandShowSpine.Checked = currentConfig.SystemConfig.Visible;
            commandWallpaperMode.Checked = currentConfig.BasicConfig.WallpaperMode;
            commandMouseClickThrough.Checked = currentConfig.BasicConfig.MouseClickThrough;
            RefreshPetMenu(Program.SpineWindow.Spine.ActiveSlot);
            RefreshFocusMenu();
            RefreshDialogMenu();
        }

        private void commandShowSpine_Click(object sender, EventArgs e)
        {
            var currentConfig = Program.CurrentConfig;
            if (commandShowSpine.Checked)
            {
                Program.SpineWindow.Visible = false;
                currentConfig.SystemConfig.Visible = false;
            }
            else
            {
                Program.SpineWindow.Visible = true;
                currentConfig.SystemConfig.Visible = true;
            }
            Program.LocalConfig = currentConfig;
        }

        private void commandWallpaperMode_Click(object sender, EventArgs e)
        {
            var currentConfig = Program.CurrentConfig;
            if (commandWallpaperMode.Checked)
            {
                Program.SpineWindow.WallpaperMode = false;
                currentConfig.BasicConfig.WallpaperMode = false;
            }
            else
            {
                Program.SpineWindow.WallpaperMode = true;
                currentConfig.BasicConfig.WallpaperMode = true;
            }
            Program.LocalConfig = currentConfig;
        }

        private void commandMouseClickThrough_Click(object sender, EventArgs e)
        {
            var currentConfig = Program.CurrentConfig;
            if (commandMouseClickThrough.Checked)
            {
                Program.SpineWindow.MouseClickThrough = false;
                currentConfig.BasicConfig.MouseClickThrough = false;
            }
            else
            {
                Program.SpineWindow.MouseClickThrough = true;
                currentConfig.BasicConfig.MouseClickThrough = true;
            }
            Program.LocalConfig = currentConfig;
        }

        private void commandSetFullScreen_Click(object sender, EventArgs e)
        {
            var screenBounds = Screen.FromHandle(Program.SpineWindow.Handle).Bounds;
            var screenPosition = screenBounds.Location;
            var screenSize = screenBounds.Size;
            Program.SpineWindow.Position = new(screenPosition.X, screenPosition.Y);
            Program.SpineWindow.Size = new((uint)screenSize.Width, (uint)screenSize.Height);
            Program.LocalConfig = Program.CurrentConfig;
        }

        private void commandResetSpine_Click(object sender, EventArgs e)
        {
            Program.SpineWindow.ResetPositionAndSize();
            Program.LocalConfig = Program.CurrentConfig;
        }

        private void commandSpineTool_Click(object sender, EventArgs e)
        {
            var spineToolPath = Path.Combine(Program.ProgramDirectory, "SpineTool.exe");
            try
            {
                Process.Start(spineToolPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"SpineTool 启动失败\n\n{ex}", Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void commandConfig_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Normal;
            this.Show();
            this.Activate();
            this.Focus();
            this.BringToFront();
        }

        private void commandAbout_Click(object sender, EventArgs e)
        {
            new AboutForm().Show();
        }

        private void commandExit_Click(object? sender, EventArgs e)
        {
            Program.LocalConfig = Program.CurrentConfig;
            Application.Exit();
        }

        /// <summary>
        /// 按当前装载的槽位重建「切换角色」菜单; 槽位当名册用时一只一项, 勾选的是正在出场的那只
        /// </summary>
        private void RefreshPetMenu(int activeSlot)
        {
            commandPets.DropDownItems.Clear();
            commandPets.Enabled = false;

            var spine = Program.SpineWindow.Spine;
            var loaded = spine.LoadedSlots.ToArray();
            if (loaded.Length == 0)
            {
                commandPets.DropDownItems.Add(new ToolStripMenuItem("(未装载骨骼)") { Enabled = false });
                return;
            }

            commandPets.Enabled = true;
            foreach (var index in loaded)
            {
                var slot = index;
                var item = new ToolStripMenuItem(PetName(spine.GetSpineSkelPath(slot)));
                item.Checked = slot == activeSlot;
                item.Click += (object? s, EventArgs e) => SwitchPet(slot);
                commandPets.DropDownItems.Add(item);
            }

            // 图层模式下所有槽位叠着画, 只在真有多只时才给这个出口
            if (loaded.Length > 1)
            {
                commandPets.DropDownItems.Add(new ToolStripSeparator());
                var layers = new ToolStripMenuItem("图层模式(全部槽位)");
                layers.Checked = activeSlot < 0;
                layers.Click += (object? s, EventArgs e) => SwitchPet(-1);
                commandPets.DropDownItems.Add(layers);
            }
        }

        private static string PetName(string? skelPath) => PetNames.Display(skelPath);

        private void SwitchPet(int slot)
        {
            var currentConfig = Program.CurrentConfig;
            Program.SpineWindow.Spine.ActiveSlot = slot;
            currentConfig.SpineConfig.ActiveSlot = Program.SpineWindow.Spine.ActiveSlot;
            // 换一只角色就换了一个包围盒, 浮窗的头顶锚点得照新的算
            Program.SpineWindow.RefreshGeometry();
            Program.LocalConfig = currentConfig;
        }

        /// <summary>
        /// 番茄钟那一项每次展开现读: 剩多久、今天做了几段都是会变的, 写死在菜单上就晚了
        /// </summary>
        private void RefreshFocusMenu()
        {
            var focus = Program.SpineWindow.Pomodoro;
            commandFocus.DropDownItems.Clear();
            commandFocus.DropDownItems.Add(new ToolStripMenuItem(
                FocusText(focus.Phase, focus.IsPaused, focus.Remaining, focus.CompletedToday,
                          focus.WorkRoundNumber, focus.RoundsPerLongBreak)) { Enabled = false });
            commandFocus.DropDownItems.Add(new ToolStripSeparator());

            var running = focus.Phase != TinyEngine.PomodoroPhase.Off;
            commandFocus.DropDownItems.Add(FocusItem(focus.IsPaused ? "继续这一段" : "开始番茄",
                                    () => focus.Start(), !running || focus.IsPaused));
            commandFocus.DropDownItems.Add(FocusItem("暂停这一段", () => focus.Pause(),
                                    running && !focus.IsPaused));
            commandFocus.DropDownItems.Add(FocusItem("跳过这一段", () => focus.Skip(), running));
            commandFocus.DropDownItems.Add(FocusItem("停止番茄钟", () => focus.Abort(), running));
            commandFocus.DropDownItems.Add(new ToolStripSeparator());
            // 和右键宠物进同一个窗, 文案照方案 §5
            commandFocus.DropDownItems.Add(FocusItem("预设小窗", () => Program.FocusPreset.OpenAtPet(), true));
        }

        /// <summary>
        /// 托盘「台词」: 一个开关 + 立刻换一条。
        /// 停留和空闲的秒数没做控件 —— 只有这一处要调节奏, 改 config.json 的 DialogConfig.ShowSeconds /
        /// GapSeconds 就行, 下次启动生效。开关是活体那份, 点完马上写盘, 不等 5 分钟那趟定时保存
        /// </summary>
        private void RefreshDialogMenu()
        {
            var dialog = Program.Dialog;
            commandDialog.DropDownItems.Clear();

            var toggle = new ToolStripMenuItem("台词气泡") { Checked = dialog.Enabled };
            toggle.Click += (object? s, EventArgs e) =>
            {
                dialog.Enabled = !dialog.Enabled;
                Program.LocalConfig = Program.CurrentConfig;
                Program.DialogBubble.OnTick();      // 关了立刻收、开了立刻数, 不等下一拍
            };
            commandDialog.DropDownItems.Add(toggle);
            commandDialog.DropDownItems.Add(new ToolStripSeparator());
            commandDialog.DropDownItems.Add(FocusItem("换一条", () => Program.DialogBubble.ShowNow(), dialog.Enabled));
        }

        private static ToolStripMenuItem FocusItem(string text, Action act, bool enabled)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (object? s, EventArgs e) => act();
            return item;
        }

        /// <summary>
        /// 托盘上那一行状态字。抽成纯函数是为了无头判据能直接喂假值对文字, 不用真等 25 分钟
        /// </summary>
        internal static string FocusText(TinyEngine.PomodoroPhase phase, bool paused, TimeSpan remaining,
                                         int completed, int round, int rounds)
        {
            if (phase == TinyEngine.PomodoroPhase.Off) return $"没在计时 · 今日 {completed}";
            // 不能用 @"mm\:ss": 那是「分」这一位(0..59), 而时长上限给到 180 —— 两小时会读成 00:00
            var left = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
            // 「本轮第几段」只进这一行文字: 这轮先补语义, 浮窗和小窗的版面一个字没动(用户拍板)
            return $"{(paused ? "已暂停" : FocusPhaseName(phase))} 剩 {left} · 本轮 {round}/{rounds} · 今日 {completed}";
        }

        internal static string FocusPhaseName(TinyEngine.PomodoroPhase phase) => phase switch
        {
            TinyEngine.PomodoroPhase.Working => "专注中",
            TinyEngine.PomodoroPhase.ShortBreak => "短休息",
            TinyEngine.PomodoroPhase.LongBreak => "长休息",
            _ => "没在计时",
        };

        /// <summary>
        /// 换阶段气泡的正文。宠物那侧已经有表情和一句语音, 气泡是给正开着别的应用、没看桌面的人留的
        /// </summary>
        internal static string FocusTip(TinyEngine.PomodoroPhase phase, int minutes, int completed) => phase switch
        {
            TinyEngine.PomodoroPhase.Working => $"开始 {minutes} 分钟, 专心",
            TinyEngine.PomodoroPhase.ShortBreak => $"这一段做完了, 今日 {completed} 个, 歇 {minutes} 分钟",
            TinyEngine.PomodoroPhase.LongBreak => $"连着做完一轮, 今日 {completed} 个, 歇 {minutes} 分钟",
            _ => "",
        };

        /// <summary>
        /// 盯着番茄钟的阶段切换, 换了就弹一句; 计数照常跟, 关气泡时不会攒出一个过期的
        /// </summary>
        private void FocusTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            var focus = Program.SpineWindow.Pomodoro;
            // 倒计时浮窗吃这一只节拍，别再另起一只（两处节拍迟早漂）。
            // 宠物沉进壁纸层或整个藏了的时候没法贴头顶，那档由浮窗自己收起来
            var pet = Program.SpineWindow;
            Program.FocusFloat.OnFocusTick(focus, pet.WallpaperMode || !pet.Visible);
            Program.FocusPreset.OnFocusTick();          // 小窗开着时那句「先停一下/继续」也得跟着阶段走
            Program.DialogBubble.OnTick();              // 台词气泡吃同一只节拍: 亮几十秒自己收, 空一段再冒下一条
            var id = focus.TransitionId;
            if (id == focusSeen) return;
            focusSeen = id;
            if (!FocusBalloon || focus.Phase == TinyEngine.PomodoroPhase.Off) return;
            ShowBalloonTip("番茄钟", FocusTip(focus.Phase, focus.PhaseMinutes, focus.CompletedToday),
                focusBalloonIcon ?? balloonIcon);
        }

        #endregion

        #region 系统设置控件事件

        private void button_SelectBalloonIconPath_Click(object sender, EventArgs e)
        {
            openFileDialog_BalloonIconPath.InitialDirectory = Path.GetDirectoryName(textBox_BalloonIconPath.Text);
            if (openFileDialog_BalloonIconPath.ShowDialog() == DialogResult.OK)
            {
                textBox_BalloonIconPath.Text = Path.GetFullPath(openFileDialog_BalloonIconPath.FileName);
            }
        }

        private void label_BalloonIcon_Click(object sender, EventArgs e)
        {
            ShowBalloonTip("叮——", "这是现在的图标噢");
        }

        #endregion

        #region 基础设置控件事件

        private void label_Position_Click(object sender, EventArgs e)
        {
            numericUpDown_PositionX.Enabled = !numericUpDown_PositionX.Enabled;
            numericUpDown_PositionY.Enabled = !numericUpDown_PositionY.Enabled;
        }
        private void label_Size_Click(object sender, EventArgs e)
        {
            numericUpDown_SizeX.Enabled = !numericUpDown_SizeX.Enabled;
            numericUpDown_SizeY.Enabled = !numericUpDown_SizeY.Enabled;
        }
        private void label_SpinePosition_Click(object sender, EventArgs e)
        {
            numericUpDown_SpinePositionX.Enabled = !numericUpDown_SpinePositionX.Enabled;
            numericUpDown_SpinePositionY.Enabled = !numericUpDown_SpinePositionY.Enabled;
        }

        private void trackBar_SpineScale_ValueChanged(object sender, EventArgs e) { label_SpineScale.Text = $"{trackBar_SpineScale.Value}"; }
        private void trackBar_Opacity_ValueChanged(object sender, EventArgs e) { label_Opacity.Text = $"{trackBar_Opacity.Value}"; }
        private void trackBar_MaxFps_ValueChanged(object sender, EventArgs e) { label_MaxFps.Text = $"{trackBar_MaxFps.Value}"; }

        private void numericUpDown_BackgroundColorR_ValueChanged(object sender, EventArgs e)
        {
            if (numericUpDown_BackgroundColorB.Value != numericUpDown_BackgroundColorR.Value)
                numericUpDown_BackgroundColorB.Value = numericUpDown_BackgroundColorR.Value;
        }
        private void numericUpDown_BackgroundColorG_ValueChanged(object sender, EventArgs e) { }
        private void numericUpDown_BackgroundColorB_ValueChanged(object sender, EventArgs e)
        {
            if (numericUpDown_BackgroundColorR.Value != numericUpDown_BackgroundColorB.Value)
                numericUpDown_BackgroundColorR.Value = numericUpDown_BackgroundColorB.Value;
        }

        #endregion

        #region Spine 设置控件事件

        private void button_SelectSkel_Click(object sender, EventArgs e)
        {
            var button = sender as Button;
            int index = int.Parse(button.Name.Substring(button.Name.Length - 1)); // 从按钮名称中提取索引
            var textBox = Controls.Find($"textBox_SkelPath{index}", true).FirstOrDefault() as TextBox;

            openFileDialog_SelectSkel.InitialDirectory = Path.GetDirectoryName(textBox.Text);
            if (openFileDialog_SelectSkel.ShowDialog() == DialogResult.OK)
            {
                textBox.Text = Path.GetFullPath(openFileDialog_SelectSkel.FileName);
            }
        }

        private void button_ClearSkel_Click(object sender, EventArgs e)
        {
            var button = sender as Button;
            int index = int.Parse(button.Name.Substring(button.Name.Length - 1)); // 从按钮名称中提取索引
            var textBox = Controls.Find($"textBox_SkelPath{index}", true).FirstOrDefault() as TextBox;
            textBox.Text = string.Empty;
        }

        #endregion

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            switch (m.Msg)
            {
                // WM_SETTINGCHANGE
                case 0x001A:
                    var useLightTheme = SystemValue.SystemUseLightTheme;
                    var resources = new System.ComponentModel.ComponentResourceManager(typeof(ConfigForm));
                    Program.PerfMonitorForm.UseLightTheme = useLightTheme;
                    notifyIcon.Icon = AppIcon.Resolve((Icon?)(useLightTheme ? resources.GetObject("$this.Icon") : resources.GetObject("notifyIcon.Icon")));
                    break;
            }
        }

    }
}
