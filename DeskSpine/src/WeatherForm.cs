using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskSpine
{
    /// <summary>
    /// 天气小卡：右键菜单挑「查看天气」开在宠物边上的那张板 —— 城名、当前温度和现象、风、今日高低，
    /// 底下再跟一句建议（按优先级挑出来的那一件；屏幕上是不知火就用她的腔说，别的角色退回中性那档）。标题「今日天气」。
    /// 和预设小窗同一个画法（一张 GDI 合成的图贴成分层窗），所以行表、排版、命中全在纯函数里，
    /// 无头判据能喂真抓回来的 JSON 对每一行的字；「字够不够大、纸够不够宽」还是得用户本人看桌面。
    /// 联网只写在 WeatherService 那一头，这扇窗只管三种下场：读取中、读到了、读不到
    /// </summary>
    public sealed class WeatherForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>板上头那行标题（用户 2026-11-03 要的是「今日天气」，卡片上摆的也确实是今天这一天的读数）</summary>
        internal const string Caption = "今日天气";

        /// <summary>还没回来的时候板上就这一行</summary>
        internal const string Loading = "读取中…";

        /// <summary>读不到的时候第一行，第二行才是为什么</summary>
        internal const string Failed = "读不到天气";

        /// <summary>一句都没给的时候顶上（取数那条腿现在不会走到，留着是不许出现空行）</summary>
        internal const string NoReason = "没给理由";

        /// <summary>纸拉到多宽都封顶：读不到时那句理由可能是别家一长串英文，不许把板子顶成一条横幅</summary>
        internal const int MaxPanelWidth = 460;

        private const int PadX = 26;
        private const int PadY = 12;
        private const int TitleY = 24;        // 和预设小窗同一张纸：顶栏那两笔墨痕占 y=17~28，标题坐 PadY 就被压着
        private const int TitleH = 44;
        private const int Gap = 8;
        private const int CloseSize = 44;     // com_btn_close 的原尺寸
        private const int NativePanelWidth = 276;     // bg_tips_3 的原始宽度
        private const int PaperMargin = 20;   // 纸自己的左边线到面板左沿的距离（同预设小窗那个数）
        private const int HeroHeight = 44;
        private const int LineHeight = 30;

        private static readonly Color PaperInk = Color.FromArgb(43, 43, 43);

        /// <summary>这一行用哪一号字。就两档：一眼要看的当前读数一大号，其余正文一号</summary>
        internal enum Face { Hero, Body }

        /// <summary>
        /// 板上的一行：字 + 用哪档字 + 这一行许不许折。行表怎么排只由 <see cref="LinesOf"/> 说了算 ——
        /// 「读到是五行、读不到是两行」这种话判据能直接对, 不用看图。
        /// <c>Wrap</c> 那一样是给底下那句建议留的：它按行盒宽折行、不参与开宽，所以板子既不会为它变宽
        /// 也不会为它换字号（放宽反而撞破用户拍的「三扇小框等宽」）
        /// </summary>
        internal readonly record struct Line(string Text, Face Face, bool Wrap = false);

        /// <summary>画和打靶共用的一份矩形。Rows 和行表一行对一行</summary>
        internal readonly record struct Spots(Rectangle Panel, Rectangle Title, Rectangle Close, Rectangle[] Rows);

        internal enum Hit { None, Close }

        private readonly FocusAssets assets = FocusAssets.Load();
        private readonly Font titleFont = new(FocusPresetForm.TitleFamily, FocusPresetForm.TitleSize,
            FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font heroFont = new(SystemFonts.MessageBoxFont!.FontFamily, 26f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly StringFormat leftText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        private readonly StringFormat centreText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        /// <summary>量字用的一张一像素底：量的和画的得是同一套 GDI+，换 TextRenderer 就差那一两个像素, 板子会挤字</summary>
        private readonly Bitmap probe = new(1, 1, PixelFormat.Format32bppArgb);

        private Spots spots = Arrange(new Line[] { new(Loading, Face.Body) }, NativePanelWidth);
        private Bitmap? panel;
        private string stateText = "";
        private WeatherService.Outcome? last;
        private bool flying;
        private bool onScreen;
        private bool wanted;
        private readonly object gate = new();

        public WeatherForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            _ = Handle;                   // 菜单那一下从渲染线程来, 转回 UI 线程要先有句柄
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
        /// 菜单那一行点下来：开在宠物边上，顺带把没回来的那一趟催一下。任何线程都能叫。
        /// 开之前先让另外两张板收干净 —— 它们抢的是同一块地方，留着就会跟着宠物一起往这儿推
        /// </summary>
        public void OpenAtPet()
        {
            Program.PetMenu?.Yield();
            Program.FocusPreset?.Yield();
            lock (gate) { wanted = true; }
            Enqueue();
        }

        /// <summary>别的面板要上这块地了：把这张收回去（连 wanted 一起清, 不然下一拍它自己又冒上来）</summary>
        public void Yield()
        {
            lock (gate) { wanted = false; }
            Enqueue();
        }

        /// <summary>开着的时候台词气泡让位 —— 抢的是宠物右侧同一块地方</summary>
        internal bool IsWanted
        {
            get { lock (gate) return wanted; }
        }

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

            EnsureFetching();
            var lines = LinesOf(last, DialogBubbleForm.ActivePet());
            var state = "";
            foreach (var l in lines) state += $"|{l.Face}:{l.Wrap}:{l.Text}";
            if (panel is null || state != stateText)
            {
                panel?.Dispose();
                // 宽只由不折的那些行定（读不到时那句理由照旧能把板子顶宽到封顶），
                // 折行拿的是这个宽减两头纸边距之后的行盒
                var width = WidthFor(lines, MeasureLine, assets.Panel?.Width ?? NativePanelWidth);
                var rows = WrapRows(lines, MeasureLine, width - 2 * PaperMargin);
                spots = Arrange(rows, width);
                panel = Compose(spots, rows);
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

        /// <summary>
        /// 手里没卡片、又没人在路上，才开一趟。读到就照缓存走（半小时内 WeatherService 不再联网），
        /// 读不到就等下次开这张板时再试 —— 所以「×掉再右键」本身就是重试那一下
        /// </summary>
        private void EnsureFetching()
        {
            lock (gate)
            {
                if (flying || last?.Card is not null) return;
                flying = true;
            }
            WeatherService.BeginFetch(got =>
            {
                lock (gate)
                {
                    flying = false;
                    last = got;
                }
                Enqueue();
            });
        }

        /// <summary>
        /// 板上的行表：没结果就一行「读取中…」；读不到两行（这板不行 + 为什么）；
        /// 读到五行 —— 上面四行是读数（城名、当前温度和现象、风、今日高低，当前那行用大号字），
        /// 第五行是把等高多出来的那段空白填上的那句建议（判断和文案都在 WeatherService，
        /// 屏幕上是不知火就用她的腔、别的角色退回中性；她那句长，<see cref="WrapRows"/> 会把它折成两行）
        /// </summary>
        internal static IReadOnlyList<Line> LinesOf(WeatherService.Outcome? got, string pet)
        {
            if (got is null) return new[] { new Line(Loading, Face.Body) };
            var outcome = got.Value;
            if (outcome.Card is null)
                return new[] { new Line(Failed, Face.Body), new Line(outcome.Reason ?? NoReason, Face.Body) };
            var c = outcome.Card.Value;
            return new[]
            {
                new Line(WeatherService.CityLine(c), Face.Body),
                new Line(WeatherService.NowLine(c), Face.Hero),
                new Line(WeatherService.WindLine(c), Face.Body),
                new Line(WeatherService.TodayLine(c), Face.Body),
                new Line(WeatherService.AdviceSentence(c, pet), Face.Body, Wrap: true),
            };
        }

        /// <summary>
        /// 把带 <c>Wrap</c> 的那几行按行盒宽折开：逐字试宽、放不下就断一行 —— 和台词气泡
        /// <see cref="DialogBubbleForm.WrapLines"/> 同一套走法（两边各写一份迟早漂）。
        /// 折出来的每一片都由构造保证进得了行盒，所以这句话既顶不宽板子也不压字
        /// </summary>
        internal static IReadOnlyList<Line> WrapRows(IReadOnlyList<Line> lines, Func<Line, Size> measure, int rowWidth)
        {
            var rows = new List<Line>();
            foreach (var line in lines)
            {
                if (!line.Wrap)
                {
                    rows.Add(line);
                    continue;
                }
                var cur = "";
                foreach (var ch in line.Text)
                {
                    var trial = cur + ch;
                    if (cur.Length > 0 && measure(new Line(trial, line.Face)).Width > rowWidth)
                    {
                        rows.Add(new Line(cur, line.Face));
                        cur = ch.ToString();
                    }
                    else cur = trial;
                }
                if (cur.Length > 0) rows.Add(new Line(cur, line.Face));
            }
            return rows.ToArray();
        }

        /// <summary>行高跟着字号走</summary>
        internal static int HeightOf(Face face) => face == Face.Hero ? HeroHeight : LineHeight;

        /// <summary>
        /// 版面按画出来的字量：最长那行加两头纸边距，不足素材原宽就用原宽，顶到 <see cref="MaxPanelWidth"/> 就封顶。
        /// 带 <c>Wrap</c> 的那些行不参与（它按行盒折，本来就不该顶宽板子）。
        /// `measure` 从外头给 —— 产品里真拿 GDI+ 量，判据喂一把假尺（按字数乘字号）就能对出宽和高
        /// </summary>
        internal static int WidthFor(IReadOnlyList<Line> lines, Func<Line, Size> measure, int nativeWidth)
        {
            var widest = 0;
            foreach (var line in lines)
                if (!line.Wrap) widest = Math.Max(widest, measure(line).Width);
            return Math.Min(MaxPanelWidth, Math.Max(nativeWidth, widest + 2 * PaperMargin));
        }

        /// <summary>
        /// 从上往下数行：标题占一头，其余每行按自己的字号占高，行与行之间留 Gap。
        /// 每行的盒子都按纸面中线居中（不是整幅宽的中线 —— 左边那 20 格是抹完碎片留下的空白）。
        /// 面板高度不跟着这几行缩：用户 2026-11-03 拍的是三扇小框整体长度一致，四行数不满就底下留空白
        /// </summary>
        internal static Spots Arrange(IReadOnlyList<Line> lines, int panelWidth)
        {
            var rows = new Rectangle[lines.Count];
            var rowW = panelWidth - 2 * PaperMargin;
            var left = CentreX(panelWidth, rowW);
            var y = TitleY + TitleH + Gap;
            for (int i = 0; i < lines.Count; i++)
            {
                rows[i] = new Rectangle(left, y, rowW, HeightOf(lines[i].Face));
                y += rows[i].Height + (i + 1 < lines.Count ? Gap : 0);
            }
            var close = new Rectangle(panelWidth - PadY - CloseSize, PadY, CloseSize, CloseSize);
            return new Spots(new Rectangle(0, 0, panelWidth, Math.Max(y - Gap + PadY, FocusPresetForm.PanelHeight(panelWidth))),
                new Rectangle(PadX, TitleY, close.X - Gap - PadX, TitleH), close, rows);
        }

        /// <summary>横向中线取纸面那一半：照整幅宽居会把内容顶到纸边上去（预设小窗那轮量的就是这件事）</summary>
        private static int CentreX(int panelWidth, int width) => Math.Max(PadX, (panelWidth + PaperMargin - width) / 2);

        private Size MeasureLine(Line line)
        {
            using var g = Graphics.FromImage(probe);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return g.MeasureString(line.Text, FontFor(line.Face), PointF.Empty, centreText).ToSize();
        }

        private Font FontFor(Face face) => face == Face.Hero ? heroFont : bodyFont;

        internal static (Hit hit, int index) HitTest(Spots spots, Point at)
            => spots.Close.Contains(at) ? (Hit.Close, -1) : (Hit.None, -1);

        /// <summary>
        /// 一张板画成一张图：底板 bg_tips_3 九宫格（比原宽就只拉中段），标题左上、×右上，行行居中。
        /// 没装机就整块退回深色圆角板, 字跟着换米白 —— 别浅底配浅字
        /// </summary>
        private Bitmap Compose(Spots s, IReadOnlyList<Line> lines)
        {
            var art = new Bitmap(s.Panel.Width, s.Panel.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(art);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            FocusArt.Crisp(g);

            var paper = assets.Panel is not null;
            using var ink = new SolidBrush(paper ? PaperInk : FocusArt.InkColour);
            if (paper) FocusArt.NineSlice(g, assets.Panel!, s.Panel, FocusArt.PanelInset);
            else
            {
                using var box = FocusArt.RoundedBox(s.Panel, 12);
                g.FillPath(ink, box);
            }

            g.DrawString(Caption, titleFont, ink, s.Title, leftText);
            DrawCentered(g, assets.Close, s.Close);
            for (int i = 0; i < lines.Count; i++)
                g.DrawString(lines[i].Text, FontFor(lines[i].Face), ink, s.Rows[i], centreText);
            return art;
        }

        /// <summary>素材缺就不画这一块, 但位置留着 —— 少一张图不该把整块板挪位</summary>
        private static void DrawCentered(Graphics g, Bitmap? tile, Rectangle box)
        {
            if (tile is null) return;
            g.DrawImage(tile, new Rectangle(box.X + (box.Width - tile.Width) / 2,
                                           box.Y + (box.Height - tile.Height) / 2, tile.Width, tile.Height));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (HitTest(spots, e.Location).hit is not Hit.None)
            {
                lock (gate) { wanted = false; }
                Enqueue();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                assets.Dispose();
                panel?.Dispose();
                probe.Dispose();
                titleFont.Dispose();
                heroFont.Dispose();
                bodyFont.Dispose();
                leftText.Dispose();
                centreText.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
