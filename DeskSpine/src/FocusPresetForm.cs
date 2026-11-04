using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskSpine
{
    /// <summary>
    /// 番茄钟预设小窗：右键宠物、或托盘「番茄钟 → 预设小窗」开在宠物旁边的面板。
    /// 一张 GDI 合成的图贴成分层窗，WinForms 控件一个都不用，所以风格能完全跟着库里的素材走。
    /// 版面、命中区、写盘那步全是纯函数，无头判据能直接喂数对；「看着挤不挤、点得准不准」只能桌面复验
    /// </summary>
    public sealed class FocusPresetForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        internal readonly record struct Preset(int Work, int Break, int LongBreak, int Cycles);

        /// <summary>三档时长。这里只给「选哪一档」，四档数字本身回设置窗改</summary>
        internal static readonly Preset[] Presets =
        {
            new(25, 5, 15, 4),
            new(45, 10, 20, 3),
            new(50, 10, 25, 3),
        };

        internal enum Hit { None, Close, Start, Switch, Preset }

        /// <summary>画和打靶共用的一份矩形 —— 两处各写一遍坐标迟早对不上。Rows 是整行的命中区, 圆点在它左边那格</summary>
        internal readonly record struct Spots(Rectangle Panel, Rectangle Title, Rectangle Close,
            Rectangle[] Rows, Rectangle[] Plates, Rectangle Switch, Rectangle Summary, Rectangle Start);

        private const int PadX = 26;
        private const int PadY = 12;
        private const int TitleY = 24;        // 标题不坐 PadY: 顶栏那两笔墨痕在装机图 y=17~28(左边那笔 x 94~109 正好盖住「钟」), 标题的墨实测从盒顶往下第 7 格才开始
        private const int Gap = 8;
        private const int TitleH = 44;
        private const int SummaryH = 24;
        private const int RadioBox = 54;      // btn_select_on 的原尺寸, 一格都不缩
        private const int RowH = 54;
        private const int PlateW = 104;       // 两位数字 + 「分」在 36 字高下量得出 101
        private const int PlateH = 44;
        private const int CloseSize = 44;     // com_btn_close 原尺寸
        private const int StartW = 150;       // com_btn_press 比 normal 宽 7 px, 取宽的那个当盒子, 两张都在里面原尺寸居中
        private const int StartH = 66;
        private const int NativePanelWidth = 276;     // bg_tips_3 的原始宽度: 横着一 squeeze 就把纸纹压扁了
        private const int PaperMargin = 20;   // 装机抹掉邻格碎片后, 纸自己的左边线到面板左沿的距离(量自 bg_tips_3 第 20 列)

        /// <summary>
        /// 右下角那一小景摆哪几枚：装机文件名、离纸右沿留多少格、落在哪一行高（照 276 宽那一版量的）。
        /// 横向从右沿倒着量，面板拉宽时它们跟着右边走，不会掉进中间那排选项和数字板上；
        /// 纵向写死 —— 面板高度是数行数出来的，不跟宽度变。
        /// 这张表摊成 internal 是给无头判据读的：「装饰件压没压到点得着的地方」不然只能靠肉眼看图。
        /// 挑了谁、落了谁（金坠子、落叶·右飘6、金色光点都试过），理由写在 bwp-assets/install_focus_ui.py 的注释里
        /// </summary>
        internal static readonly (string File, int RightGap, int Y)[] DecoSpots =
        {
            (@"deco\fengye2.png", 11, 312),     // 枫叶·纯金
            (@"deco\fengye1.png", 12, 342),     // 枫叶·紫心金边
        };

        private static readonly Color PaperInk = Color.FromArgb(43, 43, 43);

        /// <summary>标题这一款。上一轮候选表里那一格「素晴字体」其实是宋体代笔 —— 那个 font.ttf 的 cmap
        /// 里连一个「番茄钟」都没有，GDI+ 认得出家族名却没有字形，就悄悄借了别人的笔。
        /// 这里换成华文中宋：和那一格真实长相同一类的宋体脸，但它自己带全套字形，而且标题走常规面、不靠描边仿粗
        /// （这一家没有真粗面，bold_faces.py 数过）。
        /// 字形覆盖这条由 bwp-assets/out/font_cover.py --check 逐个字验，不靠运行时兜底</summary>
        internal const string TitleFamily = "华文中宋";
        internal const int TitleSize = 28;        // 20 太小：「番茄钟」只占 60/186，标题盒 44 高里上半截是空的

        private readonly FocusAssets assets = FocusAssets.Load();
        private readonly Font titleFont = new(TitleFamily, TitleSize, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        // 小字这行原来 15 常规，纸上一眼看过去要费点劲。雅黑 UI 这个家族自带真 Bold 面（不是描边仿的），
        // 17 粗在最宽那档的摘要句占 227，摘要盒照纸边距画出来是 236 —— 再上一号（19 粗 254）居中后两头就都越过纸那 20 格的边距了
        private readonly Font smallFont = new(SystemFonts.MessageBoxFont!.FontFamily, 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly StringFormat leftText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        private readonly StringFormat centreText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        private Spots spots = Arrange(Presets, NativePanelWidth);
        private Bitmap? panel;
        private string stateText = "";
        private (Hit hit, int index) pressed;
        private bool onScreen;
        private bool wanted;
        private readonly object gate = new();

        public FocusPresetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            _ = Handle;                   // 右键事件从渲染线程来, 转回 UI 线程要先有句柄
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <summary>
        /// 右键或托盘那一下：开在宠物边上。任何线程都能叫。
        /// 开之前先让功能菜单和天气卡收干净 —— 三张板抢的是同一块地方
        /// </summary>
        public void OpenAtPet()
        {
            Program.PetMenu?.Yield();
            Program.Weather?.Yield();
            lock (gate) { wanted = true; }
            Enqueue();
        }

        /// <summary>别的面板要上这块地了：把这张收回去（连 wanted 一起清, 不然下一拍它自己又冒上来）</summary>
        public void Yield()
        {
            lock (gate) { wanted = false; }
            Enqueue();
        }

        /// <summary>
        /// 小窗现在想不想开着。台词气泡问这个 —— 两张板抢的是宠物右侧同一块地方，
        /// 一起开就互相压字，所以它开着时气泡让位
        /// </summary>
        internal bool IsWanted
        {
            get { lock (gate) return wanted; }
        }

        /// <summary>开着的时候跟着宠物的秒数和阶段刷新, 不然按钮会从「先停一下」一直挂着</summary>
        public void OnFocusTick() => Enqueue();

        public void OnGeometryChanged(TinyEngine.LayeredRenderWindow.PetGeometry _) => Enqueue();

        private void Enqueue()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { }
        }

        private void Apply()
        {
            lock (gate) { if (!wanted) { Stow(); return; } }

            var focus = Program.SpineWindow.Pomodoro;
            var picked = MatchIndex(focus.WorkMinutes, focus.BreakMinutes, focus.LongBreakMinutes, focus.CyclesPerLongBreak);
            var state = $"{picked}|{Program.ConfigForm.FocusBalloon}|{focus.Phase}|{focus.IsPaused}|{pressed.hit}{pressed.index}";
            if (panel is null || state != stateText)
            {
                panel?.Dispose();
                spots = Arrange(Presets, assets.Panel?.Width ?? NativePanelWidth);
                panel = Compose(spots, picked, Program.ConfigForm.FocusBalloon,
                    focus.Phase != TinyEngine.PomodoroPhase.Off, focus.IsPaused);
                stateText = state;
            }

            var geo = Program.SpineWindow.Geometry;
            var pet = FocusArt.PetScreenRect(new Point(geo.WindowPosition.X, geo.WindowPosition.Y),
                new Size((int)geo.WindowSize.X, (int)geo.WindowSize.Y),
                new RectangleF(geo.Box.Left, geo.Box.Top, geo.Box.Width, geo.Box.Height));
            var area = Screen.FromPoint(new Point(pet.X + pet.Width / 2, pet.Y + pet.Height / 2)).WorkingArea;
            onScreen = LayeredPaint.Push(Handle, panel, FocusArt.SideBounds(pet, spots.Panel.Size, area));
        }

        private void Stow()
        {
            if (!onScreen) return;
            onScreen = false;
            LayeredPaint.Hide(Handle);
        }

        /// <summary>现在这几分钟数对上第几档; 一个都对不上就是 -1(在设置窗里自己配过)</summary>
        internal static int MatchIndex(int work, int brk, int longBreak, int cycles)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (Presets[i] == new Preset(work, brk, longBreak, cycles)) return i;
            return -1;
        }

        /// <summary>
        /// 只动番茄钟这一段, 而且是原地改 cfg.PomodoroConfig。
        /// 换成 new PomodoroConfig() 再填就等于把气泡图标、专注别睡着那两键顶回默认值 —— 上次「图层模式」就是这么中的
        /// </summary>
        internal static Config Write(Config cfg, int? preset, bool? balloon)
        {
            var target = cfg.PomodoroConfig;
            if (preset is int i)
            {
                var p = Presets[i];
                target.WorkMinutes = p.Work;
                target.BreakMinutes = p.Break;
                target.LongBreakMinutes = p.LongBreak;
                target.CyclesPerLongBreak = p.Cycles;
            }
            if (balloon is bool on) target.BalloonOnPhaseChange = on;
            return cfg;
        }

        internal static string PrimaryLabel(bool running, bool paused)
            => !running ? "开始专注" : paused ? "继续" : "先停一下";

        /// <summary>
        /// 纵向按行数出来, 横向把「选项 + 数字板」这一组摆在纸面上居中。
        /// 面板宽度用素材原宽, 所以横向九宫格其实只补纵向那一截
        /// </summary>
        internal static Spots Arrange(Preset[] presets, int panelWidth)
        {
            var rows = new Rectangle[presets.Length];
            var plates = new Rectangle[presets.Length];
            var group = RadioBox + Gap + PlateW;
            var left = CentreX(panelWidth, group);
            var y = TitleY + TitleH + Gap;
            for (int i = 0; i < presets.Length; i++)
            {
                rows[i] = new Rectangle(left, y, group, RowH);
                plates[i] = new Rectangle(left + RadioBox + Gap, y + (RowH - PlateH) / 2, PlateW, PlateH);
                y += RowH;
            }

            y += Gap;
            // 摘要这一行独享整幅纸, 盒子取「纸面宽」那么宽(236)才塞得下 17 粗的最宽那句(227);
            // 但左边不许照 PaperMargin 起 —— 那样盒子的中线是 138(整幅宽的一半), 而选项组和主按钮都按
            // CentreX 坐在纸面中线 148 上, 用户看着就是这一行比别的东西偏左。跟着同一把尺居中, 盒子挪到 30~266
            var summaryW = panelWidth - 2 * PaperMargin;
            var summary = new Rectangle(CentreX(panelWidth, summaryW), y, summaryW, SummaryH);
            y += SummaryH + Gap;
            var toggle = new Rectangle(left, y, group, RowH);
            y += RowH + Gap;
            var start = new Rectangle(CentreX(panelWidth, StartW), y, StartW, StartH);
            y += StartH + PadY;

            var close = new Rectangle(panelWidth - PadY - CloseSize, PadY, CloseSize, CloseSize);
            return new Spots(new Rectangle(0, 0, panelWidth, y),
                new Rectangle(PadX, TitleY, close.X - Gap - PadX, TitleH),
                close, rows, plates, toggle, summary, start);
        }

        /// <summary>
        /// 这张板在给定宽度下有多高 —— 功能选择小窗和天气卡照这个数对齐（用户 2026-11-03 拍：三扇小框
        /// 整体长度要一致，内容不够就下面留空白）。写成算式而不是抄 418，是因为它跟着 Presets 的行数走，
        /// 抄的那份迟早和这边对不上。
        /// </summary>
        internal static int PanelHeight(int panelWidth) => Arrange(Presets, panelWidth).Panel.Height;

        /// <summary>横向中线取纸面那一半: 左边那 20 px 是抹完碎片留下的空白, 照整幅宽居会把内容顶到纸边上去</summary>
        private static int CentreX(int panelWidth, int width) => Math.Max(PadX, (panelWidth + PaperMargin - width) / 2);

        internal static (Hit hit, int index) HitTest(Spots spots, Point at)
        {
            if (spots.Close.Contains(at)) return (Hit.Close, -1);
            if (spots.Start.Contains(at)) return (Hit.Start, -1);
            if (spots.Switch.Contains(at)) return (Hit.Switch, -1);
            for (int i = 0; i < spots.Rows.Length; i++)
                if (spots.Rows[i].Contains(at)) return (Hit.Preset, i);
            return (Hit.None, -1);
        }

        private void ComposeRow(Graphics g, Rectangle row, Rectangle plate, Preset preset, bool picked)
        {
            DrawCentered(g, picked ? assets.RadioOn : assets.RadioOff,
                new Rectangle(row.X, row.Y, RadioBox, row.Height));

            using var box = FocusArt.RoundedBox(plate, 8);
            using var bed = new SolidBrush(FocusArt.BackColour);
            using var cream = new SolidBrush(FocusArt.InkColour);
            g.FillPath(bed, box);

            var strip = FocusArt.DigitStrip($"{preset.Work}", assets.Digits);
            if (strip is null)
            {
                g.DrawString($"{preset.Work} 分", bodyFont, cream, plate, centreText);
                return;
            }

            var pad = (plate.Height - strip.Height) / 2;
            var x = plate.X + pad;
            g.DrawImage(strip, new Rectangle(x, plate.Y + pad, strip.Width, strip.Height));
            var rest = new Rectangle(x + strip.Width + FocusArt.DigitGap, plate.Y,
                                     plate.Right - x - strip.Width - FocusArt.DigitGap, plate.Height);
            g.DrawString("分", smallFont, cream, rest, leftText);
            strip.Dispose();
        }

        /// <summary>整张小窗画成一张图：底板 bg_tips_3 九宫格, 深色内板托暖族数字, 按钮和选项全按原尺寸摆</summary>
        private Bitmap Compose(Spots s, int picked, bool balloon, bool running, bool paused)
        {
            var art = new Bitmap(s.Panel.Width, s.Panel.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(art);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            FocusArt.Crisp(g);

            // 素材没装机就整块退回深色圆角板, 字也跟着换成米白 —— 别浅底配浅字
            var paper = assets.Panel is not null;
            using (var ink = new SolidBrush(paper ? PaperInk : FocusArt.InkColour))
            using (var cream = new SolidBrush(FocusArt.InkColour))
            {
                if (paper) FocusArt.NineSlice(g, assets.Panel!, s.Panel, FocusArt.PanelInset);
                else
                {
                    using var box = FocusArt.RoundedBox(s.Panel, 12);
                    g.FillPath(ink, box);
                }

                DrawDeco(g, s.Panel);

                g.DrawString("番茄钟", titleFont, ink, s.Title, leftText);
                DrawCentered(g, assets.Close, s.Close);

                for (int i = 0; i < s.Rows.Length; i++)
                    ComposeRow(g, s.Rows[i], s.Plates[i], Presets[i], i == picked);

                var summary = picked < 0
                    ? "现在这几分钟没对上任何一档"
                    : $"短休 {Presets[picked].Break} · 长假 {Presets[picked].LongBreak} · 连做 {Presets[picked].Cycles} 段";
                g.DrawString(summary, smallFont, ink, s.Summary, centreText);

                DrawCentered(g, balloon ? assets.RadioOn : assets.RadioOff,
                    new Rectangle(s.Switch.X, s.Switch.Y, RadioBox, s.Switch.Height));
                g.DrawString("换阶段弹气泡", bodyFont, ink,
                    new Rectangle(s.Switch.X + RadioBox + Gap, s.Switch.Y, s.Switch.Width - RadioBox - Gap, s.Switch.Height), leftText);

                var pressedStart = pressed.hit == Hit.Start;
                DrawCentered(g, pressedStart ? assets.ButtonPressed : assets.ButtonNormal, s.Start);
                g.DrawString(PrimaryLabel(running, paused), bodyFont, cream, s.Start, centreText);
            }
            return art;
        }

        /// <summary>按 DecoSpots 那张表原尺寸摆装饰件；没装机就一枚都不画，别拿别的图凑</summary>
        private void DrawDeco(Graphics g, Rectangle panel)
        {
            foreach (var (file, gap, y) in DecoSpots)
                if (assets.Deco.TryGetValue(file, out var tile))
                    g.DrawImageUnscaled(tile, panel.Right - gap - tile.Width, y);
        }

        /// <summary>素材缺就不画这一块, 但位置留着 —— 少一张图不该把整块面板挪位</summary>
        private static void DrawCentered(Graphics g, Bitmap? tile, Rectangle box)
        {
            if (tile is null) return;
            g.DrawImage(tile, new Rectangle(box.X + (box.Width - tile.Width) / 2,
                                           box.Y + (box.Height - tile.Height) / 2, tile.Width, tile.Height));
        }

        private void Act((Hit hit, int index) at)
        {
            switch (at.hit)
            {
                case Hit.Close:
                    lock (gate) { wanted = false; }
                    break;
                case Hit.Preset:
                    Commit(Write(Program.CurrentConfig, at.index, null));
                    break;
                case Hit.Switch:
                    Commit(Write(Program.CurrentConfig, null, !Program.ConfigForm.FocusBalloon));
                    break;
                case Hit.Start:
                    var focus = Program.SpineWindow.Pomodoro;
                    if (focus.Phase != TinyEngine.PomodoroPhase.Off && !focus.IsPaused) focus.Pause();
                    else focus.Start();
                    break;
            }
            Enqueue();
        }

        /// <summary>
        /// 写盘走 LocalConfig(就一句 Save, 不会重装载骨骼), 再把这一段现推到引擎。
        /// 最后把整份配置回灌设置窗: 不回灌的话它那个旧勾选下次「应用」会把这一下顶回去
        /// </summary>
        private void Commit(Config cfg)
        {
            Program.LocalConfig = cfg;
            Program.ApplyFocus(cfg.PomodoroConfig);
            Program.ConfigForm.Value = cfg;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = HitTest(spots, e.Location);
            if (pressed.hit is not Hit.None) Enqueue();     // 按下那张图要换个底
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var was = pressed;
            pressed = (Hit.None, -1);
            var at = HitTest(spots, e.Location);
            if (at == was && at.hit is not Hit.None) Act(at);   // 按下和抬起落在同一块才算一次点
            else Enqueue();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                assets.Dispose();
                panel?.Dispose();
                titleFont.Dispose();
                bodyFont.Dispose();
                smallFont.Dispose();
                leftText.Dispose();
                centreText.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
