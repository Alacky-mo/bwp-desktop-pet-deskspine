using Microsoft.Win32;

namespace DeskSpine
{
    public static class Program
    {
#if DEBUG
        public const string ProgramName = "DeskSpine_d";
#else
        public const string ProgramName = "DeskSpine";
#endif

        /// <summary>
        /// 对外可见的那几个字：托盘提示、「关于」窗标题、重复启动那个框的标题。
        /// 和 ProgramName 分成两个的理由：ProgramName 同时是数据目录名(%LOCALAPPDATA%\DeskSpine)、
        /// 单实例 Mutex 的名字、注册表自启那条目的值名 —— 跟着界面名字一起改，等于把用户已有的配置和开机自启弄丢。
        /// exe 那头的名字是 BwpDesktopPet（csproj 里的 AssemblyName），这一串是配给它的中文显示名。
        /// </summary>
        public const string DisplayName = "百闻牌桌宠";

        /// <summary>
        /// 程序可执行文件所在目录
        /// </summary>
        public static readonly string ProgramDirectory = Path.GetDirectoryName(Application.ExecutablePath);

        /// <summary>
        /// 程序数据存放目录
        /// </summary>
        public static readonly string ProgramDataDirectory = Path.Combine(SystemValue.LocalAppdataDirectory, ProgramName);

        /// <summary>
        /// 程序资源目录
        /// </summary>
        public static readonly string ProgramResourceDirectory = Path.Combine(ProgramDirectory, "res");

        /// <summary>
        /// 程序配置文件路径
        /// </summary>
        public static readonly string ProgramConfigPath = Path.Combine(ProgramDataDirectory, "config.json");

        public static PerfMonitor.PerfMonitorForm PerfMonitorForm { get; private set; }     // 性能浮窗
        public static FocusFloatForm FocusFloat { get; private set; }                       // 专注倒计时浮窗, 贴宠物头顶
        public static FocusPresetForm FocusPreset { get; private set; }                     // 番茄钟预设小窗, 开在宠物边上
        public static DialogBubbleForm DialogBubble { get; private set; }                   // 台词气泡, 也贴宠物右侧
        public static PetMenuForm PetMenu { get; private set; }                              // 右键弹的功能选择小窗, 番茄钟和天气都从它进
        public static WeatherForm Weather { get; private set; }                              // 天气小卡, 菜单第二行开它, 也贴宠物右侧
        public static DialogConfig Dialog { get; private set; } = new();                    // 台词气泡那段设置的活体: 节拍每秒读它, 不碰磁盘
        public static ConfigForm ConfigForm { get; private set; }                           // 设置窗口 (主窗口)
        public static TinyEngine.SpineRenderWindow SpineWindow { get; private set; }        // Spine 窗口
        private static Mutex programMutex;                                                  // 程序单一启动锁
        private static System.Timers.Timer configSaveTimer = new(300000) { AutoReset = true };

        /// <summary>
        /// 程序入口
        /// </summary>
        [STAThread]
        public static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            programMutex = new Mutex(true, ProgramName, out bool createNew);
            if (!createNew)
            {
                MessageBox.Show("程序已在运行, 请勿重复启动", DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(ProgramDataDirectory)) 
                Directory.CreateDirectory(ProgramDataDirectory);

            // 随 release 附件带过来的素材在这一头才认得路：自己从源码建的包没 res\spine，直接跳过。
            BundledRoster.SeedIfFirstRun();

            PerfMonitorForm = new() { UseLightTheme = SystemValue.SystemUseLightTheme };
            SpineWindow = new TinyEngine.SpineRenderWindow(SpineConfig.SlotCount);
            FocusFloat = new FocusFloatForm();
            FocusPreset = new FocusPresetForm();
            DialogBubble = new DialogBubbleForm();
            PetMenu = new PetMenuForm();
            Weather = new WeatherForm();
            // 右键单击是全场唯一空闲的手势, 给功能选择小窗当入口; 拖动过发不出 Clicked, 所以挪宠物不会顺手开一扇窗。
            // 番茄钟从这一轮起多一次点击（用户点头的方案）: 右键先弹「番茄钟 / 查看天气」, 挑一行才开那一扇。
            // 倒计时浮窗不靠手势开关: 计时中就自己贴在头顶露着。事件在渲染线程发, 每扇窗各自转回 UI 线程
            SpineWindow.Clicked += (SFML.Window.Mouse.Button b) =>
            {
                if (b == SFML.Window.Mouse.Button.Right) PetMenu.OpenAtPet();
            };
            SpineWindow.GeometryChanged += FocusFloat.OnGeometryChanged;
            SpineWindow.GeometryChanged += FocusPreset.OnGeometryChanged;
            SpineWindow.GeometryChanged += DialogBubble.OnGeometryChanged;
            SpineWindow.GeometryChanged += PetMenu.OnGeometryChanged;
            SpineWindow.GeometryChanged += Weather.OnGeometryChanged;
            ConfigForm = new ConfigForm();
            InitFromConfig(LocalConfig);

            // 开启一个 Timer 定时保存配置
            configSaveTimer.Elapsed += (object? s, System.Timers.ElapsedEventArgs e) => LocalConfig = CurrentConfig;
            configSaveTimer.Enabled = true;

            Application.Run();
        }

        /// <summary>
        /// 是否开机自启
        /// </summary>
        public static bool AutoRun
        {
            get
            {
                var exePath = $"\"{Application.ExecutablePath}\"";
                var ret = false;
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    ret = runKey.GetValue(ProgramName, "").ToString() == exePath;
                return ret;
            }
            set
            {
                var exePath = $"\"{Application.ExecutablePath}\"";
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (value)
                        runKey.SetValue(ProgramName, exePath);
                    else
                        runKey.DeleteValue(ProgramName, false);
                }
            }
        }

        /// <summary>
        /// 根据程序实时运行状态获取配置
        /// </summary>
        public static Config CurrentConfig
        {
            get
            {
                var config = new Config();

                // 系统设置
                config.SystemConfig.AutuRun = AutoRun;
                config.SystemConfig.Visible = SpineWindow.Visible;
                config.SystemConfig.BalloonIconPath = ConfigForm.BalloonIconPath;
                config.SystemConfig.HourlyChime = ConfigForm.HourlyChime;

                // 基础设置
                var position = SpineWindow.Position;
                var size = SpineWindow.Size;
                var spPosition = SpineWindow.Spine.Position;
                config.BasicConfig.WallpaperMode = SpineWindow.WallpaperMode;
                config.BasicConfig.MouseClickThrough = SpineWindow.MouseClickThrough;
                config.BasicConfig.PositionX = position.X;
                config.BasicConfig.PositionY = position.Y;
                config.BasicConfig.SizeX = size.X;
                config.BasicConfig.SizeY = size.Y;
                config.BasicConfig.SpinePositionX = spPosition.X;
                config.BasicConfig.SpinePositionY = spPosition.Y;
                config.BasicConfig.SpineFlip = SpineWindow.Spine.Flip;
                config.BasicConfig.SpineScale = SpineWindow.Spine.Scale;
                config.BasicConfig.Opacity = SpineWindow.Opacity;
                config.BasicConfig.MaxFps = SpineWindow.MaxFps;
                config.BasicConfig.BackgroundColor = SpineWindow.BackgroudColor;
                config.BasicConfig.SpineUsePMA = SpineWindow.Spine.UsePremultipliedAlpha;

                // Spine 设置
                config.SpineConfig.SpineVersion = SpineWindow.Spine.Version;
                config.SpineConfig.InteractMode = SpineWindow.Spine.AnimatorType;
                config.SpineConfig.ActiveSlot = SpineWindow.Spine.ActiveSlot;
                for (int i = 0; i < SpineConfig.SlotCount; i++)
                {
                    config.SpineConfig.SetSkelPath(i, SpineWindow.Spine.GetSpineSkelPath(i));
                }

                // 番茄钟设置
                var focus = SpineWindow.Pomodoro;
                config.PomodoroConfig.WorkMinutes = focus.WorkMinutes;
                config.PomodoroConfig.BreakMinutes = focus.BreakMinutes;
                config.PomodoroConfig.LongBreakMinutes = focus.LongBreakMinutes;
                config.PomodoroConfig.CyclesPerLongBreak = focus.CyclesPerLongBreak;
                config.PomodoroConfig.BlockSleepWhileWorking = focus.BlockSleepWhileWorking;
                // 这两个开关设置窗上没有控件, 从引擎现读: 漏一行就是「存得进去、读不出来」(desk 第 16 条咬这个)
                config.PomodoroConfig.AutoStartBreak = focus.AutoStartBreak;
                config.PomodoroConfig.AutoStartWork = focus.AutoStartWork;
                config.PomodoroConfig.BalloonOnPhaseChange = ConfigForm.FocusBalloon;
                config.PomodoroConfig.BalloonIconPath = ConfigForm.FocusBalloonIconPath;

                // 台词气泡整节就是活体 Dialog。漏这一行 = 「配了丢进去」, 下次装载把它抹平
                config.DialogConfig = Dialog;

                // 定位缓存没有活体：它的事实在网络那一头，写只在 WeatherService 的锁里，这里现抄一份进配置段
                var (weatherCity, weatherLat, weatherLon, weatherAt) = WeatherService.Snapshot();
                config.WeatherConfig = new WeatherConfig
                {
                    CityName = weatherCity,
                    Latitude = weatherLat,
                    Longitude = weatherLon,
                    ResolvedAt = weatherAt,
                };

                return config;
            }

            set
            {
                // XXX: 不确定是否需要 Lock/Unlock

                // 系统设置
                AutoRun = value.SystemConfig.AutuRun;
                ConfigForm.BalloonIconPath = value.SystemConfig.BalloonIconPath;
                ConfigForm.HourlyChime = value.SystemConfig.HourlyChime;

                // 基础设置
                SpineWindow.WallpaperMode = value.BasicConfig.WallpaperMode;
                SpineWindow.MouseClickThrough = value.BasicConfig.MouseClickThrough;
                SpineWindow.Position = new(value.BasicConfig.PositionX, value.BasicConfig.PositionY);
                SpineWindow.Size = new(value.BasicConfig.SizeX, value.BasicConfig.SizeY);
                SpineWindow.Spine.Position = new(value.BasicConfig.SpinePositionX, value.BasicConfig.SpinePositionY);
                SpineWindow.Spine.Flip = value.BasicConfig.SpineFlip;
                SpineWindow.Spine.Scale = value.BasicConfig.SpineScale;
                SpineWindow.Opacity = value.BasicConfig.Opacity;
                SpineWindow.MaxFps = value.BasicConfig.MaxFps;
                SpineWindow.BackgroudColor = value.BasicConfig.BackgroundColor;
                SpineWindow.Spine.UsePremultipliedAlpha = value.BasicConfig.SpineUsePMA;

                // Spine 设置
                SpineWindow.Spine.AnimatorType = value.SpineConfig.InteractMode;
                try
                {
                    SpineWindow.Spine.Version = value.SpineConfig.SpineVersion;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        ConfigForm,
                        $"版本 {value.SpineConfig.SpineVersion} 加载失败\n\n{ex}",
                        "Spine 资源加载失败",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }

                for (int i = 0; i < SpineConfig.SlotCount; i++)
                {
                    var skelPath = value.SpineConfig.GetSkelPath(i);
                    try 
                    { 
                        SpineWindow.Spine.SetSpine(skelPath, i); 
                    }
                    catch (Exception ex) 
                    { 
                        MessageBox.Show(ConfigForm, $"{skelPath} 加载失败\n\n{ex}", "Spine 资源加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); 
                    }
                }

                // 最后调整窗口可见性
                SpineWindow.Visible = value.SystemConfig.Visible;
                // 槽位当角色名册用, 只放行出场的那一格
                SpineWindow.Spine.ActiveSlot = value.SpineConfig.ActiveSlot;
                ApplyFocus(value.PomodoroConfig);
                Dialog = value.DialogConfig;
                WeatherService.Seed(value.WeatherConfig, DateTimeOffset.Now);
                // 缩放、骨骼位置、出场槽位和刚装上的骨骼都动过包围盒, 头顶锚点要重算一次才跟得上
                SpineWindow.RefreshGeometry();

                // 保存本地
                LocalConfig = CurrentConfig;
            }
        }

        /// <summary>
        /// 获取和保存本地配置
        /// </summary>
        public static Config LocalConfig
        {
            get { var v = new Config(); v.Load(ProgramConfigPath); return v; }
            set => value.Save(ProgramConfigPath);
        }

        /// <summary>
        /// 从指定的配置进行初始化
        /// </summary>
        private static void InitFromConfig(Config value)
        {
            // 系统配置
            ConfigForm.BalloonIconPath = value.SystemConfig.BalloonIconPath; 
            ConfigForm.HourlyChime = value.SystemConfig.HourlyChime;

            // 基础配置
            SpineWindow.WallpaperMode = value.BasicConfig.WallpaperMode;
            SpineWindow.MouseClickThrough = value.BasicConfig.MouseClickThrough;
            SpineWindow.Position = new(value.BasicConfig.PositionX, value.BasicConfig.PositionY);
            SpineWindow.Size = new(value.BasicConfig.SizeX, value.BasicConfig.SizeY);
            SpineWindow.Spine.Position = new(value.BasicConfig.SpinePositionX, value.BasicConfig.SpinePositionY);
            SpineWindow.Spine.Flip = value.BasicConfig.SpineFlip;
            SpineWindow.Spine.Scale = value.BasicConfig.SpineScale;
            SpineWindow.Opacity = value.BasicConfig.Opacity;
            SpineWindow.MaxFps = value.BasicConfig.MaxFps;
            SpineWindow.BackgroudColor = value.BasicConfig.BackgroundColor;
            SpineWindow.Spine.UsePremultipliedAlpha = value.BasicConfig.SpineUsePMA;

            // Spine 设置
            SpineWindow.Spine.AnimatorType = value.SpineConfig.InteractMode;
            try 
            { 
                SpineWindow.Spine.Version = value.SpineConfig.SpineVersion; 
            }
            catch (Exception ex) 
            { 
                MessageBox.Show(
                    ConfigForm, 
                    $"版本 {value.SpineConfig.SpineVersion} 加载失败\n\n{ex}", 
                    "Spine 资源加载失败", 
                    MessageBoxButtons.OK, 
                    MessageBoxIcon.Error
                );
            }

            for (int i = 0; i < SpineConfig.SlotCount; i++)
            {
                var skelPath = value.SpineConfig.GetSkelPath(i);
                try 
                { 
                    SpineWindow.Spine.SetSpine(skelPath, i); 
                }
                catch (Exception ex) 
                { 
                    MessageBox.Show(ConfigForm, $"{skelPath} 加载失败\n\n{ex}", "Spine 资源加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); 
                }
            }

            // 最后调整窗口可见性
            SpineWindow.Visible = value.SystemConfig.Visible;
            // 槽位当角色名册用, 只放行出场的那一格
            SpineWindow.Spine.ActiveSlot = value.SpineConfig.ActiveSlot;
            ApplyFocus(value.PomodoroConfig);
            Dialog = value.DialogConfig;
            WeatherService.Seed(value.WeatherConfig, DateTimeOffset.Now);
            // 骨骼是这一刻才有的, 头顶锚点得重算一次, 不然浮窗停在「还没有包围盒」那份快照上
            SpineWindow.RefreshGeometry();
        }

        /// <summary>
        /// 番茄钟的时长一份住在引擎一份在配置文件, 这里从配置一次推过去。
        /// 「现在到底有没有在计时」是用户点出来的, 不属于配置, 所以不碰开始/暂停/停止
        /// </summary>
        internal static void ApplyFocus(PomodoroConfig value)
        {
            var focus = SpineWindow.Pomodoro;
            focus.WorkMinutes = value.WorkMinutes;
            focus.BreakMinutes = value.BreakMinutes;
            focus.LongBreakMinutes = value.LongBreakMinutes;
            focus.CyclesPerLongBreak = value.CyclesPerLongBreak;
            focus.BlockSleepWhileWorking = value.BlockSleepWhileWorking;
            focus.AutoStartBreak = value.AutoStartBreak;
            focus.AutoStartWork = value.AutoStartWork;
            ConfigForm.FocusBalloon = value.BalloonOnPhaseChange;
            ConfigForm.FocusBalloonIconPath = value.BalloonIconPath;
        }
    }
}
