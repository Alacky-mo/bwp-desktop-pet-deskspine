using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeskSpine
{
    /// <summary>
    /// 「查看天气」那一腿的取数：先用 IP 定个大致位置，再让 Open-Meteo 的地名接口把英文名换成中文，最后取今天的读数。
    /// 解析、换算、成句全是「喂字符串出结果」的纯函数 —— 无头判据直接拿真抓回来的 JSON 对答案；
    /// 网络只写在下面那一截，任何一跳翻车都收成一句中文理由交回调用方，不往外抛。
    /// 这是桌宠第一次主动联网：四个端点全 HTTPS，一个 key 都不要，断了也只是卡片上多一行「读不到」
    /// </summary>
    internal static class WeatherService
    {
        /// <summary>每一跳最多等 6 秒（连接和整趟都是）。小窗只有一行「读取中…」，等太久它就永远停在那一行</summary>
        internal const int TimeoutSeconds = 6;

        /// <summary>位置缓存 12 小时：IP 归属地不会一天变几回，但换台机器、插根网线就得重新认一次</summary>
        internal const int LocationTtlMinutes = 720;

        /// <summary>读数缓存 30 分钟：天气本身变化慢，再勤也就那样，主要是别一开小窗就发三趟请求</summary>
        internal const int ReadingTtlMinutes = 30;

        /// <summary>
        /// 两个 IP 源轮着试。第一个是主力，第二个兜它挂掉的场合。
        /// 都只给英文名：唯一能在 HTTPS 下给中文城市名那家（ip-api）免费档只有 http，443 直接 403 —— 所以中文名去下一跳换
        /// </summary>
        internal static readonly string[] IpUrls = { "https://ipwho.is/", "https://api.ip.sb/geoip" };

        /// <summary>地名接口：英文名进，中文名出，顺带回它自己那套坐标</summary>
        internal const string NameUrlFormat =
            "https://geocoding-api.open-meteo.com/v1/search?name={0}&count=1&language=zh&format=json";

        /// <summary>取数接口。当前四项 + 今日高低两项，`timezone=auto` 让服务器按本地日子算「今日」</summary>
        internal const string ForecastUrlFormat =
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}"
            + "&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m"
            + "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";

        /// <summary>一座城：中文名（换不到就是英文名）+ 拿去问天气的坐标</summary>
        internal readonly record struct Place(string City, double Latitude, double Longitude);

        /// <summary>
        /// 卡片要的那几样，全取整、全换算完了 —— 小窗只管把行摆上去，不再做任何算术。
        /// `WindFrom` 是八方位那一个字（风从哪边来），`WindGrade` 是蒲福级。
        /// `Code` 留的是 WMO 那张表的原始代码：现象词只是它的中文名，建议要跟着代码判 ——
        /// 哪天「小雨」改口叫别的，不该把「出门带伞」那一档悄悄判丢
        /// </summary>
        internal readonly record struct Card(string City, int TempC, int Code, string Phenomenon,
            string WindFrom, int WindGrade, int LowC, int HighC, DateTimeOffset ResolvedAt);

        /// <summary>一次取数的两种下场：一张卡片，或者一句中文理由。永远不会有第三种（抛出去）</summary>
        internal readonly record struct Outcome(Card? Card, string? Reason);

        /// <summary>WMO 天气代码 → 中文现象词。Open-Meteo 就发这一套码，一家之言所以照它文档列全</summary>
        private static readonly Dictionary<int, string> Phenomena = new()
        {
            [0] = "晴",
            [1] = "少云",
            [2] = "局部多云",
            [3] = "阴",
            [45] = "雾",
            [48] = "雾凇",
            [51] = "小毛毛雨",
            [53] = "毛毛雨",
            [55] = "大毛毛雨",
            [56] = "小冻毛毛雨",
            [57] = "大冻毛毛雨",
            [61] = "小雨",
            [63] = "中雨",
            [65] = "大雨",
            [66] = "小冻雨",
            [67] = "大冻雨",
            [71] = "小雪",
            [73] = "中雪",
            [75] = "大雪",
            [77] = "雪粒",
            [80] = "小阵雨",
            [81] = "中阵雨",
            [82] = "急阵雨",
            [85] = "小阵雪",
            [86] = "大阵雪",
            [95] = "雷阵雨",
            [96] = "雷阵雨伴小冰雹",
            [99] = "雷阵雨伴冰雹",
        };

        /// <summary>表里没有的码（以后多出来的一律）也得好端端说句话，不能空着</summary>
        internal const string PhenomenonFallback = "其他天象";

        /// <summary>八方位，从正北起顺时针。第 0 格盖住 337.5°~22.5° 那一整块</summary>
        internal static readonly string[] Compass8 = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

        /// <summary>蒲福风级各档的上限（km/h）：小于等于第 i 个就是 i 级，越过最后一个封顶 12 级</summary>
        private static readonly int[] BeaufortUpperKmh = { 1, 5, 11, 19, 28, 38, 49, 61, 74, 88, 102, 117 };

        private const string BadJson = "回来的不是数据";

        private static readonly HttpClient Http =
            new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds) })
            { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

        private static readonly object gate = new();
        private static Place? place;
        private static DateTimeOffset placeAt = DateTimeOffset.MinValue;
        private static Card? reading;
        private static int running;

        // ---- 纯函数：无头判据量的就是这些 ----

        /// <summary>
        /// 温度取整：20.0 → 20，12.6 → 13。朝外走（AwayFromZero），免得到 .5 的时候两头含糊
        /// </summary>
        internal static int Round(double c) => (int)Math.Round(c, MidpointRounding.AwayFromZero);

        /// <summary>天气代码 → 中文现象词，表里没有的退成 <see cref="PhenomenonFallback"/></summary>
        internal static string Phenomenon(int code)
            => Phenomena.TryGetValue(code, out var name) ? name : PhenomenonFallback;

        /// <summary>
        /// 风向角 → 八方位。给的是「风从哪边来」的角度（180° 就是南风），每格 45°、中心压在正方位上，
        /// 所以 22.5° 这种缝口归东北。负数和超过一圈都先折回 0~360
        /// </summary>
        internal static string Compass(double degrees)
        {
            var d = degrees % 360;
            if (d < 0) d += 360;
            var index = (int)((d + 22.5) / 45) % 8;
            return Compass8[index];
        }

        /// <summary>风速 km/h → 蒲福级，0~12。13.6 km/h 落在第三档</summary>
        internal static int Beaufort(double kmh)
        {
            for (int i = 0; i < BeaufortUpperKmh.Length; i++)
                if (kmh <= BeaufortUpperKmh[i]) return i;
            return BeaufortUpperKmh.Length;
        }

        /// <summary>卡片第一行：城名</summary>
        internal static string CityLine(Card c) => c.City;

        /// <summary>卡片第二行：当前温度和现象，「20° 阴」</summary>
        internal static string NowLine(Card c) => $"{c.TempC}° {c.Phenomenon}";

        /// <summary>卡片第三行：风从哪边来、几级，「西南风 3 级」</summary>
        internal static string WindLine(Card c) => $"{c.WindFrom}风 {c.WindGrade} 级";

        /// <summary>卡片第四行：今天的最低到最高，「今日 13° ~ 20°」</summary>
        internal static string TodayLine(Card c) => $"今日 {c.LowC}° ~ {c.HighC}°";

        /// <summary>
        /// 天象分成六组，<see cref="Phenomena"/> 那 28 个码一个不落地各归一家（判据逐码验这件事）。
        /// 建议照着代码组走而不是照着现象词走 —— 现象词是给用户看的那张中文名，代码才是接口发回来的那份
        /// </summary>
        private static readonly int[] FrozenCodes = { 56, 57, 66, 67 };            // 冻毛毛雨、冻雨
        private static readonly int[] SnowCodes = { 71, 73, 75, 77, 85, 86 };      // 雪、雪粒、阵雪
        private static readonly int[] ThunderCodes = { 95, 96, 99 };
        private static readonly int[] RainCodes = { 51, 53, 55, 61, 63, 65, 80, 81, 82 };
        private static readonly int[] FogCodes = { 45, 48 };
        private static readonly int[] CalmSkyCodes = { 0, 1, 2, 3 };               // 晴到阴：天上没东西掉下来

        private static bool In(int[] codes, int code) => Array.IndexOf(codes, code) >= 0;

        /// <summary>
        /// 板底下那句建议的十五种下场。判断只写这一份（<see cref="PickNote"/>），两档语域各一张文案表 ——
        /// 上一版是三行带前缀的罗列（出行：/ 穿衣：/ 生活：），用户 2026-11-03 拍的是「换成一句话」，
        /// 所以现在每次只说按优先级挑出来的那一件，另外两样不再各占一行（晾衣、屋里加被那两条因此没了下文）
        /// </summary>
        internal enum Note
        {
            Icing, Snow, Thunder, Rain, Fog,                            // 天象：会摔着的那几条压在一切提醒前面
            Windy,                                                      // 六级往上
            Scorching,                                                  // 当前三十度往上
            MildWalk,                                                   // 晴到阴且当前在 20~28 之间
            WideSwing,                                                  // 今日最高最低差十度往上
            Overcoat, Jacket, LongSleeves, LightLayers, ThinTop, Airy,  // 穿衣六档，按「当前和今日最低里更低的那个」落
        }

        /// <summary>只有这一只用她的腔写建议（台词池那 22 条全是她的独白）。名字和托盘「切换角色」那行字同一把尺（<see cref="PetNames.Display"/>）</summary>
        internal const string Shiranui = "不知火";

        /// <summary>
        /// 挑哪一句：天象 &gt; 风大 &gt; 热 &gt; 晴好温度 &gt; 早晚温差 &gt; 穿衣那六档。
        /// 三个阈值（20~28°、六级风、温差十度）一个字没动 —— 用户 2026-11-03 拍的是「先看效果再调」。
        /// 穿衣排在最后是有意的：它逢卡必有一句，排在它后头的任何一句都永远不会被念出来
        /// </summary>
        internal static Note PickNote(Card c)
        {
            if (In(FrozenCodes, c.Code)) return Note.Icing;
            if (In(SnowCodes, c.Code)) return Note.Snow;
            if (In(ThunderCodes, c.Code)) return Note.Thunder;
            if (In(RainCodes, c.Code)) return Note.Rain;
            if (In(FogCodes, c.Code)) return Note.Fog;
            if (c.WindGrade >= 6) return Note.Windy;
            if (c.TempC >= 30) return Note.Scorching;
            if (In(CalmSkyCodes, c.Code) && c.TempC >= 20 && c.TempC <= 28) return Note.MildWalk;
            if (c.HighC - c.LowC >= 10) return Note.WideSwing;
            return DressNote(c);
        }

        /// <summary>穿衣档只看当前温度和今日最低里更低的那一个 —— 人出门多在早晚，照中午那档穿会冻着</summary>
        private static Note DressNote(Card c) => Math.Min(c.TempC, c.LowC) switch
        {
            <= 0 => Note.Overcoat,
            <= 8 => Note.Jacket,
            <= 15 => Note.LongSleeves,
            <= 22 => Note.LightLayers,
            <= 27 => Note.ThinTop,
            _ => Note.Airy,
        };

        // 两张表都逐个列全十五种下场、不留 `_ =>`：加一格忘了配文案，运行时那一下会抛，
        // desk 第 24 层「两档各逐格点名」那条跟着红（这条靠判据兜，不靠编译器 —— CS8524 对枚举一律报, 留着它认不出真漏了谁）
#pragma warning disable CS8524
        /// <summary>中性那档：别的角色（还有图层模式那种说不出是哪只的）念这一份</summary>
        internal static string NeutralNote(Note n) => n switch
        {
            Note.Icing => "路上结冰，走慢点",
            Note.Snow => "有雪，路面滑",
            Note.Thunder => "雷阵雨，躲开空旷处",
            Note.Rain => "出门带伞",
            Note.Fog => "能见度低，少开车",
            Note.Windy => "风大，收好帽子",
            Note.Scorching => "防晒多喝水",
            Note.MildWalk => "适合出门走走",
            Note.WideSwing => "早晚温差大，备件衣",
            Note.Overcoat => "穿厚外套，戴围巾",
            Note.Jacket => "加件外套",
            Note.LongSleeves => "长袖加外套",
            Note.LightLayers => "长袖就够",
            Note.ThinTop => "穿薄一点",
            Note.Airy => "穿透气的",
        };

        /// <summary>
        /// 她那一档：同一条判断换个说法。语气照那 22 条台词池量出来的样子写 —— 称「你」6 条、17 句不带结尾标点、
        /// 「呢 / 吧 / 吗」在池子里只出现在**分句**尾（12 条），整句末尾一条都没有；下面把语气词放在句尾的三句是往池子外面多走的一步，
        /// 用户嫌做作就退到分句尾。词是新写的，没抄池子里任何一条原文
        /// </summary>
        internal static string HerNote(Note n) => n switch
        {
            Note.Icing => "地上结了冰，你走慢些，别摔着",
            Note.Snow => "今日有雪，路面滑，落脚轻一些",
            Note.Thunder => "要落雷了，别在空旷处站着呢",
            Note.Rain => "出门把那把伞带上，别淋着了",
            Note.Fog => "起了雾，你看不远，今天就少开车",
            Note.Windy => "风这样大，帽子收好，别叫它吹跑了",
            Note.Scorching => "日头毒，水多喝几口，防晒也别省",
            Note.MildWalk => "这样的天最宜出去走走，你不去吗",
            Note.WideSwing => "早晚差得多，多带一件外衫吧",
            Note.Overcoat => "冷得厉害，厚外套和围巾都戴上",
            Note.Jacket => "风还凉，外套再加一件",
            Note.LongSleeves => "长袖外头再套件外套就好",
            Note.LightLayers => "一件长袖够了，别捂着",
            Note.ThinTop => "穿薄一点，热天别硬扛",
            Note.Airy => "穿透气的，别热亏了自己",
        };
#pragma warning restore CS8524

        /// <summary>板上那一句的全名：屏幕上是她就用她的腔，其余角色退回中性。全从卡上已有的数算出来，一趟都不多联网</summary>
        internal static string AdviceSentence(Card c, string pet)
            => pet == Shiranui ? HerNote(PickNote(c)) : NeutralNote(PickNote(c));

        /// <summary>
        /// 离解析那一刻过了多久还算新。时间戳跑到未来（改过系统钟）算旧 —— 宁可多问一次也别拿旧的当真
        /// </summary>
        internal static bool IsFresh(DateTimeOffset resolvedAt, DateTimeOffset now, int ttlMinutes)
        {
            var age = now - resolvedAt;
            return age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(ttlMinutes);
        }

        /// <summary>
        /// IP 源的回包。两颗源共同形状：city + latitude + longitude；ipwho.is 多一个 success 位，false 就当作没查着。
        /// 坐标不在地球上（0,0 这种占位、或者写歪了的）一律拒 —— 拿它去问天气只会问回另一座城
        /// </summary>
        internal static (Place? place, string? reason) ParseIp(string? json)
        {
            if (Json(json) is not JsonObject root) return (null, BadJson);
            if (root.TryGetPropertyValue("success", out var flag) && flag is not null
                && flag.GetValueKind() == JsonValueKind.False)
                return (null, "定位源查不着这地方");
            if (!Text(root, "city", out var city)) return (null, "定位没给城市名");
            if (!Number(root, "latitude", out var lat) || !Number(root, "longitude", out var lon))
                return (null, "定位没给坐标");
            if (lat is < -90 or > 90 || lon is < -180 or > 180) return (null, "坐标不在地球上");
            return (new Place(city, lat, lon), null);
        }

        /// <summary>
        /// 地名接口的回包：results 里第一个就是最像的那座城。
        /// 换不到中文名就用英文名接着往下走 —— 名字这一跳不该把整张卡片拖黄，用户认得 Jininan 也认得济南
        /// </summary>
        internal static Place ParseName(string? json, Place fallback)
        {
            if (Json(json) is not JsonObject root) return fallback;
            if (Child(root, "results") is not JsonArray list || list.Count == 0) return fallback;
            if (list[0] is not JsonObject hit) return fallback;
            if (!Text(hit, "name", out var name)) return fallback;
            if (!Number(hit, "latitude", out var lat) || !Number(hit, "longitude", out var lon)) return fallback;
            if (lat is < -90 or > 90 || lon is < -180 or > 180) return fallback;
            return new Place(name, lat, lon);
        }

        /// <summary>
        /// 取数接口的回包。少哪一项就说哪一项，别一句「读不到」让用户猜。
        /// `now` 从外头进来：判据要能对着一刻钟前那个点验新鲜度，函数里不能自己看钟
        /// </summary>
        internal static (Card? card, string? reason) ParseForecast(string? json, string city, DateTimeOffset now)
        {
            if (Json(json) is not JsonObject root) return (null, BadJson);
            var cur = Child(root, "current") as JsonObject;
            if (cur is null) return (null, "没回当前这一组");
            if (!Number(cur, "temperature_2m", out var temp)) return (null, "当前温度没读到");
            if (!Number(cur, "weather_code", out var code)) return (null, "天气代码没读到");
            if (!Number(cur, "wind_speed_10m", out var wind)) return (null, "风速没读到");
            if (!Number(cur, "wind_direction_10m", out var deg) || deg is < 0 or >= 360)
                return (null, "风向没读到");
            var daily = Child(root, "daily") as JsonObject;
            var high = First(daily, "temperature_2m_max");
            var low = First(daily, "temperature_2m_min");
            if (high is null) return (null, "今日最高没读到");
            if (low is null) return (null, "今日最低没读到");
            return (new Card(city, Round(temp), (int)code, Phenomenon((int)code), Compass(deg), Beaufort(wind),
                             Round(low.Value), Round(high.Value), now), null);
        }

        /// <summary>
        /// 开机（和每次配置推下去）把盘里那份位置缓存灌回内存，省掉那一趟 IP 查询。过期的不收 ——
        /// 收了就等于上周那台机器还坐在上周那座城
        /// </summary>
        internal static void Seed(WeatherConfig? cache, DateTimeOffset now)
        {
            if (cache?.CityName is not { Length: > 0 } city) return;
            var lat = cache.Latitude;
            var lon = cache.Longitude;
            if (!double.IsFinite(lat) || !double.IsFinite(lon)) return;
            if (lat is < -90 or > 90 || lon is < -180 or > 180) return;
            if (!IsFresh(cache.ResolvedAt, now, LocationTtlMinutes)) return;
            lock (gate)
            {
                place = new Place(city, lat, lon);
                placeAt = cache.ResolvedAt;
            }
        }

        /// <summary>
        /// 现下手里这座城和它定下来的那一刻，给小窗抄进配置段用（后台线程不碰 config，写盘在 UI 线程上做）。
        /// 没定着就是全 null
        /// </summary>
        internal static (string? city, double lat, double lon, DateTimeOffset at) Snapshot()
        {
            lock (gate) return (place?.City, place?.Latitude ?? 0, place?.Longitude ?? 0, placeAt);
        }

        // ---- 网络：只有这一截 ----

        /// <summary>
        /// 开一趟取数，回-call 在后台线程上（小窗自己 BeginInvoke 回 UI 线程）。
        /// 已经有一趟在跑就不另开：两条链会同回一张卡片，白跑一趟网络
        /// </summary>
        internal static void BeginFetch(Action<Outcome> done)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                Outcome outcome;
                try
                {
                    outcome = await Fetch().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 兜底：这层不把异常交给调用方 —— 不然小窗就停在「读取中…」那一行不动了
                    outcome = new Outcome(null, $"{ex.GetType().Name} {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref running, 0);
                }
                done(outcome);
            });
        }

        private static async Task<Outcome> Fetch()
        {
            var now = DateTimeOffset.Now;
            lock (gate)
            {
                if (reading is Card warm && IsFresh(warm.ResolvedAt, now, ReadingTtlMinutes))
                    return new Outcome(warm, null);
            }

            // 位置过老了就重新认一趟：程序连开两天，IP 归属地跟着人换了网也得跟得上
            Place? known = null;
            lock (gate)
                if (place is { } held && IsFresh(placeAt, now, LocationTtlMinutes)) known = held;
            if (known is not { } here)
            {
                var located = await Locate().ConfigureAwait(false);
                if (located.place is not { } fresh) return new Outcome(null, located.reason);
                here = fresh;
                lock (gate)
                {
                    place = fresh;
                    placeAt = DateTimeOffset.Now;
                }
            }

            var got = await Read(here).ConfigureAwait(false);
            if (got.card is null) return new Outcome(null, got.reason);
            lock (gate) reading = got.card;
            return new Outcome(got.card, null);
        }

        /// <summary>定位：两颗 IP 源轮着试，拿到英文名再问一次地名接口换中文</summary>
        private static async Task<(Place? place, string? reason)> Locate()
        {
            string? lastReason = null;
            foreach (var url in IpUrls)
            {
                var got = await Get(url).ConfigureAwait(false);
                if (got.json is null)
                {
                    lastReason = got.reason;
                    continue;
                }
                var parsed = ParseIp(got.json);
                if (parsed.place is not { } raw)
                {
                    lastReason = parsed.reason;
                    continue;
                }
                var named = await Get(string.Format(CultureInfo.InvariantCulture, NameUrlFormat,
                                Uri.EscapeDataString(raw.City))).ConfigureAwait(false);
                return (ParseName(named.json, raw), null);
            }
            return (null, lastReason ?? "定位没回来");
        }

        /// <summary>读数：坐标进，卡片出。城名直接用手里那份，不指望天气接口认字</summary>
        private static async Task<(Card? card, string? reason)> Read(Place at)
        {
            var url = string.Format(CultureInfo.InvariantCulture, ForecastUrlFormat,
                at.Latitude.ToString("F4", CultureInfo.InvariantCulture),
                at.Longitude.ToString("F4", CultureInfo.InvariantCulture));
            var got = await Get(url).ConfigureAwait(false);
            if (got.json is null) return (null, got.reason);
            return ParseForecast(got.json, at.City, DateTimeOffset.Now);
        }

        private static async Task<(string? json, string? reason)> Get(string url)
        {
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, $"这一跳回了 {resp.StatusCode}");
                return (await resp.Content.ReadAsStringAsync().ConfigureAwait(false), null);
            }
            catch (TaskCanceledException) { return (null, "这一跳太久，不等了"); }
            catch (HttpRequestException) { return (null, "路上不通"); }
            catch (Exception ex) { return (null, $"这一跳翻车（{ex.GetType().Name}）"); }
        }

        // ---- 读 JSON 的那几只小抄子：缺字段一律算「没读到」，不许抛 ----

        /// <summary>HTML 错误页、验证码页、空身子都会在这儿变成 null</summary>
        private static JsonNode? Json(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static JsonNode? Child(JsonNode? node, string key)
            => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

        private static bool Number(JsonObject? o, string key, out double value)
        {
            value = double.NaN;
            var node = Child(o, key);
            if (node is null) return false;
            try { value = node.GetValue<double>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
            return double.IsFinite(value);
        }

        private static bool Text(JsonObject? o, string key, out string text)
        {
            text = "";
            var node = Child(o, key);
            if (node is null) return false;
            try { text = node.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
            if (text.Length == 0) return false;
            return true;
        }

        /// <summary>数组里第一个数。daily 那两项都是「一个日子一个值」的数组，只要头一个</summary>
        private static double? First(JsonNode? parent, string key)
        {
            if (Child(parent, key) is not JsonArray list || list.Count == 0) return null;
            if (list[0] is not { } node) return null;
            try
            {
                var v = node.GetValue<double>();
                return double.IsFinite(v) ? v : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
        }
    }
}
