using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DeskSpine
{
    public class BackgroundColorConverter : JsonConverter<SFML.Graphics.Color>
    {
        public override SFML.Graphics.Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // 读取 RGB 值，忽略 A
            var jsonDocument = JsonDocument.ParseValue(ref reader);
            var jsonObject = jsonDocument.RootElement;

            try
            {
                // 返回 Color 并将 Alpha 设置为 0
                byte r = jsonObject.GetProperty("R").GetByte();
                byte g = jsonObject.GetProperty("G").GetByte();
                byte b = jsonObject.GetProperty("B").GetByte();
                return new(r, g, b, 0);
            }
            catch (KeyNotFoundException)
            {
                return new(128, 128, 128, 0);
            }
        }

        public override void Write(Utf8JsonWriter writer, SFML.Graphics.Color value, JsonSerializerOptions options)
        {
            // 序列化 R、G、B 值，忽略 A
            writer.WriteStartObject();
            writer.WriteNumber("R", value.R);
            writer.WriteNumber("G", value.G);
            writer.WriteNumber("B", value.B);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// 系统设置
    /// </summary>
    public class SystemConfig
    {
        [JsonIgnore]
        public bool AutuRun { get; set; } = false;
        public bool Visible { get; set; } = true;
        /// <summary>
        /// 整点报时那颗气泡图标：2026-11-03 用户改口，和番茄钟共用同一颗红达摩
        /// （此前 2026-10-29 拍的是「报时和番茄钟分两颗」，那份记录在 docs/交接报告.md 达摩那一节）。
        /// 默认值写在属性初始化器上、不写在 <see cref="Config"/> 的构造函数里：`Load` 是**整段**换掉 SystemConfig 的
        /// （`SystemConfig = config.SystemConfig` 那一行），config.json 里有这一段但没这个键时拿到的是一个新 new
        /// 出来的对象，构造函数那份默认值会被冲掉 —— 番茄钟那颗踩过同一个坑。
        /// </summary>
        public string? BalloonIconPath { get; set; } =
            Path.Combine(Program.ProgramResourceDirectory, @"image\focus_damo.ico");
        public bool HourlyChime = true;
    }

    /// <summary>
    /// 基础设置
    /// </summary>
    public class BasicConfig
    {
        public bool WallpaperMode { get; set; } = false;
        public bool MouseClickThrough { get; set; } = false;
        public int PositionX { get; set; } = 0;
        public int PositionY { get; set; } = 0;
        public uint SizeX { get; set; } = 1000;
        public uint SizeY { get; set; } = 1000;
        public float SpinePositionX { get; set; } = 0;
        public float SpinePositionY { get; set; } = 0;
        public bool SpineFlip { get; set; } = false;
        public float SpineScale { get; set; } = 1f;
        public byte Opacity { get; set; } = 255;
        public uint MaxFps { get; set; } = 30;
        [JsonConverter(typeof(BackgroundColorConverter))] // 结构体不会自动保存字段, 需要用自定义的转换器
        public SFML.Graphics.Color BackgroundColor { get; set; } = new(128, 128, 128, 0);
        public bool SpineUsePMA { get; set; } = false;

    }

    /// <summary>
    /// Spine 设置
    /// </summary>
    public class SpineConfig
    {
        public const int SlotCount = 10;
        private string?[] skelPaths = new string?[SlotCount];

        public Spine.SpineVersion SpineVersion { get; set; } = Spine.SpineVersion.V38;
        public TinyEngine.AnimatorType InteractMode { get; set; } = TinyEngine.AnimatorType.AzurLaneSD;

        /// <summary>
        /// 出场的槽位。-1 = 所有槽位当图层一起显示; 0~9 = 槽位当角色名册, 只显示这一格
        /// </summary>
        public int ActiveSlot { get; set; } = -1;

        public string? SkelPath0 { get => skelPaths[0]; set => skelPaths[0] = value; }
        public string? SkelPath1 { get => skelPaths[1]; set => skelPaths[1] = value; }
        public string? SkelPath2 { get => skelPaths[2]; set => skelPaths[2] = value; }
        public string? SkelPath3 { get => skelPaths[3]; set => skelPaths[3] = value; }
        public string? SkelPath4 { get => skelPaths[4]; set => skelPaths[4] = value; }
        public string? SkelPath5 { get => skelPaths[5]; set => skelPaths[5] = value; }
        public string? SkelPath6 { get => skelPaths[6]; set => skelPaths[6] = value; }
        public string? SkelPath7 { get => skelPaths[7]; set => skelPaths[7] = value; }
        public string? SkelPath8 { get => skelPaths[8]; set => skelPaths[8] = value; }
        public string? SkelPath9 { get => skelPaths[9]; set => skelPaths[9] = value; }

        public string? GetSkelPath(int index)
        {
            if (index > SlotCount) throw new ArgumentOutOfRangeException(nameof(index), $"Index must less than {SlotCount}");
            if (string.IsNullOrEmpty(skelPaths[index]))
                return null;
            return skelPaths[index];
        }

        public void SetSkelPath(int index, string? value)
        {
            if (index > SlotCount) throw new ArgumentOutOfRangeException(nameof(index), $"Index must less than {SlotCount}");
            skelPaths[index] = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    /// <summary>
    /// 番茄钟设置
    /// </summary>
    public class PomodoroConfig
    {
        public int WorkMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int LongBreakMinutes { get; set; } = 15;

        /// <summary>连做几个番茄才给一次长休息</summary>
        public int CyclesPerLongBreak { get; set; } = 4;

        /// <summary>
        /// 专注跑完要不要自己接上休息。关了就停在「休息待开始」等用户点一下 ——
        /// 引擎里不另开状态, 到点仍然换脸、仍然记番茄, 只是那一段装好不走
        /// </summary>
        public bool AutoStartBreak { get; set; } = true;

        /// <summary>休息跑完要不要自己开下一段专注。默认跟现状一致(到点自己串), 想改成等用户点由这里关</summary>
        public bool AutoStartWork { get; set; } = true;

        /// <summary>专注期间不让宠物闲置入睡</summary>
        public bool BlockSleepWhileWorking { get; set; } = true;

        /// <summary>换阶段时弹托盘气泡</summary>
        public bool BalloonOnPhaseChange { get; set; } = true;

        /// <summary>
        /// 番茄钟自己那颗气泡图标。整点报时和它共用一张会串味（一个是"到点了"一个是"该休息了"），
        /// 所以单独一个键；读不出来时界面会退回共用那颗，不会因此不弹。
        /// 默认值写在这里而不是 <see cref="Config"/> 的构造函数里：`Load` 是**整段**换掉 PomodoroConfig 的,
        /// 老 config.json 没这个键时会得到一个本类 new 出来的对象, 构造里那份默认值就被冲没了 —— 图标会悄悄退回报时那颗钟。
        /// </summary>
        public string? BalloonIconPath { get; set; } =
            Path.Combine(Program.ProgramResourceDirectory, @"image\focus_damo.ico");
    }

    /// <summary>
    /// 台词气泡设置：宠物右侧那只说话泡的节奏
    /// </summary>
    public class DialogConfig
    {
        /// <summary>
        /// 默认值写在这一行的初始化上，不写进 <see cref="Config"/> 的构造函数：`Load` 是「整段」换掉这一节的，
        /// 老 config.json 没有这几个键时会拿到一个本类 new 出来的对象，构造里那份默认值会被冲成关的。
        /// 和 PomodoroConfig.BalloonIconPath 同一件事。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>一条台词停多少秒就自己收起来（2026-11-02 用户改口：45 太长，15 秒够读完）</summary>
        public int ShowSeconds { get; set; } = 15;

        /// <summary>收起之后空多少秒再冒下一条（亮的时间砍到 1/3，空档跟着从 135 缩到 60，不然一分钟才出一条）</summary>
        public int GapSeconds { get; set; } = 60;
    }

    /// <summary>
    /// 「查看天气」的定位缓存：上一回认出来的城、它的坐标、认出来的那一刻。
    /// 这不是用户设的开关，所以设置窗里没有它的一行 —— 只有 WeatherService 取数成功那一刻写它，开机那一回读它。
    /// 一个都没认着就整段留空（CityName 是 null），下一次开小窗重新定位
    /// </summary>
    public class WeatherConfig
    {
        /// <summary>中文城名（地名接口换回来的）。换不到中文就是 IP 源那个英文名 —— 卡片认这个字段有没有，不认坐标</summary>
        public string? CityName { get; set; }

        /// <summary>问天气用的纬度。0 是「还没定位过」的占位，0,0 那点在几内亚湾，不许拿它去发请求</summary>
        public double Latitude { get; set; }

        /// <summary>问天气用的经度，和 Latitude 同一句占位约定</summary>
        public double Longitude { get; set; }

        /// <summary>定位成功那一刻。太旧就不收（<see cref="WeatherService"/> 那条 12 小时的线），免得换台机器还认在上座城</summary>
        public DateTimeOffset ResolvedAt { get; set; }
    }

    /// <summary>
    /// DeskSpine 设置
    /// </summary>
    public class Config
    {
        private static JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

        public SystemConfig SystemConfig { get; set; } = new();
        public BasicConfig BasicConfig { get; set; } = new();
        public SpineConfig SpineConfig { get; set; } = new();
        public PomodoroConfig PomodoroConfig { get; set; } = new();
        public DialogConfig DialogConfig { get; set; } = new();
        public WeatherConfig WeatherConfig { get; set; } = new();

        /// <summary>
        /// 创建配置新实例并拥有合适的默认值
        /// </summary>
        public Config()
        {
            // 名册出厂是空的: 上游自带的三组演示骨骼(碧蓝航线后宅小人/动态立绘、明日方舟动态立绘)不属于本项目, 已删。
            // 装哪几只由 setup_pet.py 写 SkelPath0..9, 或在小窗里逐格指认。
        }

        /// <summary>
        /// 从本地文件加载配置
        /// </summary>
        public bool Load(string configPath)
        {
            if (File.Exists(configPath))
            {
                try
                {
                    string jsonString = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<Config>(jsonString);

                    if (config != null)
                    {
                        if (config.SystemConfig is not null) SystemConfig = config.SystemConfig;
                        if (config.BasicConfig is not null) BasicConfig = config.BasicConfig;
                        if (config.SpineConfig is not null) SpineConfig = config.SpineConfig;
                        if (config.PomodoroConfig is not null) PomodoroConfig = config.PomodoroConfig;
                        if (config.DialogConfig is not null) DialogConfig = config.DialogConfig;
                        if (config.WeatherConfig is not null) WeatherConfig = config.WeatherConfig;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading config file: {ex}");
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 保存配置至本地文件
        /// </summary>
        public bool Save(string configPath)
        {
            try
            {
                string jsonString = JsonSerializer.Serialize(this, jsonOptions);
                File.WriteAllText(configPath, jsonString);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config file: {ex.Message}");
                return false;
            }
        }
    }
}
