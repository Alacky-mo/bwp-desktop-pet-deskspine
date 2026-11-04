using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskSpine
{
    /// <summary>
    /// 不知火的台词气泡：贴在宠物旁边的一只说话泡，隔一阵换一条台词，读完就自己收起来。
    /// 走 <see cref="LayeredPaint"/> 逐像素贴，和倒计时浮窗同一套画法。
    ///
    /// 四条约束记在这儿，改版面的人容易踩：
    /// 1) 气泡那张图**不参与拉伸**。它上下两条边是斜着收尖的，横竖分开拉会把斜边拉歪，
    ///    所以版面是死尺寸（装机时按 1.15 放大过一次，运行时 1:1 贴）。
    /// 2) 台词放不进正文区就不该进池子 —— 折行上限由 <see cref="MaxLines"/> 说清楚，
    ///    desk 判据拿真池子逐条验，装不下的那条会把闸门拦红。
    /// 3) 弹台词**不碰表情和动画**。这是用户明确要的：说话归说话，宠物那侧一个字都不动。
    /// 4) 摆到宠物左边时**只镜像美术件，字永远正排**。整张水平翻会把汉字翻反 ——
    ///    预览脚本第一版就栽在这儿，产品别再犯。
    /// </summary>
    public sealed class DialogBubbleForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>画布尺寸 = 装机那张气泡 366x170 加上图集自带的透明留白（右边 13、下边 18）</summary>
        public const int CanvasWidth = 379;
        public const int CanvasHeight = 188;

        /// <summary>气泡图 1:1 贴在这</summary>
        public static readonly Point BubbleAt = new(13, 18);

        /// <summary>家纹印记（说话人印记）1:1 贴在左上；镜像那一侧跟着翻到右上</summary>
        public static readonly Point CrestAt = new(5, 5);

        /// <summary>说话人名起点（正文左边对齐；镜像时按名字宽度翻过去）</summary>
        public static readonly Point NameAt = new(62, 16);

        /// <summary>正文区：起点 + 宽高。装机放大那一档定下的 280x110</summary>
        public static readonly Rectangle TextArea = new(62, 55, 280, 110);

        /// <summary>正文和名字都按 19px 排（预览图用户就是照这个字号点的版，产品正文下限那条是 17px）</summary>
        public const int BodyPx = 19;

        /// <summary>一行占多高：19 的字 + 8 的缝，和预览同一套</summary>
        public const int LineAdvance = 27;

        /// <summary>正文区能排几行 —— 110 / 27 向下取整</summary>
        public static int MaxLines => TextArea.Height / LineAdvance;

        /// <summary>
        /// 气泡**看得见的墨**离宠物框多远。
        /// 画布左边到墨还有 14 px 的透明边（装机图自己带的），拿画布定位就等于白送 14 px，
        /// 所以这一档按墨算：<see cref="BubbleBounds"/> 用的是 <see cref="DialogAssets.Ink"/>
        /// </summary>
        public const int SideGap = 4;

        /// <summary>气泡墨底沿比头顶锚点高多少：尾巴从这儿垂下来才像「从她嘴里出来」</summary>
        public const int HeadLift = 5;

        /// <summary>字色，和预览同一支（深墨，压在奶白纸上）</summary>
        public static readonly Color InkColour = Color.FromArgb(58, 44, 40);

        /// <summary>
        /// 名牌那套色。不是随手挑的：金取库里分隔线 (254,236,174) 和名牌条 biaoqian_01 (197,146,28)
        /// 两头拉渐变，红是达摩那支红压深（太粉压不住金字）—— 用户拍的是「烫金 + 大红」
        /// </summary>
        internal static readonly Color PlateTop = Color.FromArgb(198, 48, 44);
        internal static readonly Color PlateBottom = Color.FromArgb(146, 24, 30);
        internal static readonly Color PlateEdge = Color.FromArgb(252, 228, 152);
        internal static readonly Color GoldTop = Color.FromArgb(255, 244, 196);
        internal static readonly Color GoldBottom = Color.FromArgb(206, 150, 32);
        internal static readonly Color GoldOutline = Color.FromArgb(120, 74, 12);
        internal const int PlatePadX = 11, PlatePadY = 5, PlateTip = 12;

        /// <summary>开机到第一条台词之间的等待：登场动画还没跳完就顶上一句话不好看</summary>
        public const int BootDelay = 10;

        private readonly DialogAssets assets = DialogAssets.Load();
        private readonly Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, BodyPx,
                                            FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly StringFormat layout = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap };
        private readonly Random rng = new();

        private Bitmap? artRight;
        private Bitmap? artLeft;
        private string shownText = "";
        private string artText = "";
        private bool onScreen;
        private bool showing;
        private int remaining = BootDelay;
        private int lastIndex = -1;
        private bool openedOnce;
        private int pendingTicks;
        private readonly object gate = new();

        public DialogBubbleForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;      // 整张窗由 UpdateLayeredWindow 逐像素贴
            _ = Handle;                   // 节拍和几何事件都从别的线程来，转回 UI 线程要先有句柄
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
        /// 每秒从 ConfigForm 那只共用节拍过来一次。别处再起一只定时器就等着两处节拍漂。
        /// 秒数和开关直接读 <see cref="Program.Dialog"/>（活体那份，不落盘）——
        /// <c>Program.LocalConfig</c> 的 getter 每次都重开文件读一遍，节拍里不能碰
        /// </summary>
        public void OnTick()
        {
            lock (gate) { pendingTicks++; }
            Enqueue();
        }

        /// <summary>宠物挪了、换角色了都要重贴一次，但这一趟不吃节拍、不换台词</summary>
        public void OnGeometryChanged(TinyEngine.LayeredRenderWindow.PetGeometry _) => Enqueue();

        /// <summary>托盘上「现在换一条」那一下：不等空闲到头，立刻出一条</summary>
        public void ShowNow()
        {
            lock (gate) { forceNext = true; }
            Enqueue();
        }

        private bool forceNext;

        private void Enqueue()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// 两档来回跳：亮着数 ShowSeconds，收起数 GapSeconds，到头换另一边。
        /// 不合格（开关关了 / 素材没装机 / 宠物藏了 / 右侧被菜单或小窗占着 / 屏幕上换成了别只角色）就收起整张表停着数，
        /// 回来接着数而不是从头数 —— 壁纸模式进出一趟不该把台词节奏重置一遍
        /// </summary>
        private void Apply()
        {
            int ticks; bool force;
            lock (gate)
            {
                ticks = pendingTicks; pendingTicks = 0;
                force = forceNext; forceNext = false;
            }

            var cfg = Program.Dialog;
            var pet = Program.SpineWindow;
            if (!NextVisible(cfg.Enabled, assets.Ready,
                    pet.WallpaperMode || !pet.Visible,
                    Program.FocusPreset.IsWanted || Program.PetMenu.IsWanted || Program.Weather.IsWanted,
                    PoolIsThisPet(assets.Speaker, ActivePet())))
            {
                Stow();
                return;
            }

            if (force)
            {
                BeginLine(cfg);
                Render();
                return;
            }

            var opened = false;
            for (int i = 0; i < ticks; i++)
            {
                var next = Step(showing, remaining, cfg.ShowSeconds, cfg.GapSeconds);
                showing = next.Showing;
                remaining = next.Remaining;
                opened |= next.JustOpened;
            }
            if (!showing) Stow();
            if (opened) BeginLine(cfg);
            if (showing) Render();
        }

        /// <summary>
        /// 一秒一格的那张表：亮着数满就收起、空一段，空够了就冒下一条。
        /// 抽成纯函数是为了无头判据能把「15 秒后自己消失、再空 60 秒」整段数完，
        /// 不用真坐在屏幕前等一个循环
        /// </summary>
        internal static (bool Showing, int Remaining, bool JustOpened) Step(
            bool showing, int remaining, int show, int gap)
        {
            var left = remaining - 1;
            if (left > 0) return (showing, left, false);
            // 数到头的这两边都给下限 1: 配置文件里被手改成 0 或负数时表会卡在「每拍都翻档」
            return showing ? (false, Math.Max(1, gap), false)
                           : (true, Math.Max(1, show), true);
        }

        /// <summary>挑一条、把表拨到「亮着数 ShowSeconds」。画哪张交给 Render（它才知道现在在哪一侧）</summary>
        private void BeginLine(DialogConfig cfg)
        {
            lastIndex = PickLine(assets.Lines, lastIndex, !openedOnce, rng);
            openedOnce = true;
            shownText = lastIndex >= 0 ? assets.Lines[lastIndex].Text : "";
            showing = true;
            remaining = Math.Max(1, cfg.ShowSeconds);
        }

        /// <summary>亮着的那张按宠物现在的位置重贴一次</summary>
        private void Render()
        {
            var pet = PetRect();
            var head = Program.SpineWindow.Geometry.HeadAnchor;
            var area = Screen.FromPoint(new Point(pet.X + pet.Width / 2, pet.Y + pet.Height / 2)).WorkingArea;
            var place = BubbleBounds(pet, new Point(head.X, head.Y),
                new Size(CanvasWidth, CanvasHeight), assets.Ink, area);
            var art = ArtFor(shownText, place.Mirrored);
            onScreen = LayeredPaint.Push(Handle, art, place.Bounds);
        }

        /// <summary>
        /// 两侧各存一张，作废看 <see cref="artText"/>（那两张画的是哪条台词），不能看 shownText：
        /// BeginLine 已先把 shownText 拨成新词，拿传进来的 text 和它比是恒等 —— 同一侧就永远命中旧图
        /// </summary>
        private Bitmap ArtFor(string text, bool mirror)
        {
            var slot = mirror ? artLeft : artRight;
            if (slot is not null && text == artText) return slot;
            if (text != artText)
            {
                artRight?.Dispose(); artLeft?.Dispose();
                artRight = artLeft = null;
                artText = text;
            }
            var art = Compose(text, mirror);
            if (mirror) artLeft = art; else artRight = art;
            return art;
        }

        private void Stow()
        {
            if (!onScreen) return;
            onScreen = false;
            LayeredPaint.Hide(Handle);
        }

        /// <summary>
        /// 气泡摆在宠物右边、墨底沿压过头顶一点（尾巴在气泡左下，这样才指得回宠物）。
        /// 右边放不下就翻到左边，并且**只镜像美术件**（<see cref="Compose"/> 的 mirror 那一路）。
        /// 定位按墨不按画布：画布那 14 px 透明边不该算进间距
        /// </summary>
        public static (Rectangle Bounds, bool Mirrored) BubbleBounds(Rectangle pet, Point head, Size panel,
                                                                    Rectangle ink, Rectangle workArea,
                                                                    int gap = SideGap, int lift = HeadLift)
        {
            var x = pet.Right + gap - ink.X;
            var mirrored = x + panel.Width > workArea.Right;
            if (mirrored) x = pet.Left - gap - (panel.Width - ink.X);
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - panel.Width));
            var y = head.Y - lift - ink.Bottom;
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - panel.Height));
            return (new Rectangle(x, y, panel.Width, panel.Height), mirrored);
        }

        /// <summary>装机图里「看得见的墨」那一块（alpha ≥ 200）。透明边和淡影子都不算</summary>
        public static Rectangle InkOf(Bitmap art)
        {
            var x0 = int.MaxValue; var y0 = int.MaxValue; var x1 = -1; var y1 = -1;
            for (var y = 0; y < art.Height; y++)
                for (var x = 0; x < art.Width; x++)
                {
                    if (art.GetPixel(x, y).A < 200) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            return x1 < 0 ? Rectangle.Empty : new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        /// <summary>画布内一个左上点水平镜像后的左上点（那件/那块宽 w）</summary>
        internal static int Mx(int x, int w, bool mirror) => mirror ? CanvasWidth - x - w : x;

        /// <summary>
        /// 开机头一条走「开场」那组，之后整池随机；连着两条同一个人设的话不好看，所以避开上一条。
        /// 池子只有一条时无限返回它。
        /// </summary>
        internal static int PickLine(IReadOnlyList<DialogLine> lines, int lastIndex, bool firstRound, Random rng)
        {
            if (lines.Count == 0) return -1;
            if (firstRound)
            {
                var openers = new List<int>();
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Group == "开场") openers.Add(i);
                if (openers.Count > 0) return openers[rng.Next(openers.Count)];
            }
            if (lines.Count == 1) return 0;
            int pick;
            do { pick = rng.Next(lines.Count); } while (pick == lastIndex);
            return pick;
        }

        /// <summary>
        /// 该不该显示。开关关着、池子空着或气泡图没装机（<see cref="DialogAssets.Ready"/>），
        /// 宠物沉进壁纸层或整个藏了，都不弹。
        /// 宠物右侧那块地有一张板占着（功能菜单、预设小窗、天气卡）就也不弹：它仨和气泡抢的是同一块地方。
        /// 屏幕上不是池子说的那只角色（<see cref="PoolIsThisPet"/>）也不弹。
        /// </summary>
        internal static bool NextVisible(bool enabled, bool hasLines, bool stowed, bool panelOpen, bool thisPet)
            => enabled && hasLines && !stowed && !panelOpen && thisPet;

        /// <summary>
        /// 池子说的是谁，屏幕上就得正是那只：那 22 条全是不知火的独白、名牌也写着「不知火」，
        /// 切到千姬还接着弹就是拿不知火的家纹念不知火的词。
        /// 名字两边同一把尺 —— 托盘「切换角色」那行字走的就是 <see cref="PetNames.Display"/>，池子第一列是同一个写法。
        /// 说话人为空（池子空、装机没跑）、屏幕上说不出是哪只（图层模式几格叠着画）、名字对不上 —— 都不弹，
        /// 宁可闭嘴也别念错人。
        /// </summary>
        internal static bool PoolIsThisPet(string speaker, string pet)
            => speaker.Length > 0 && pet.Length > 0 && speaker == pet;

        /// <summary>屏幕上出场那只角色的名字；图层模式（ActiveSlot=-1，几格叠着画）没有「哪一只」，给空串。天气卡那句建议用她的腔还是中性腔也认这一个数</summary>
        internal static string ActivePet()
        {
            var spine = Program.SpineWindow.Spine;
            var slot = spine.ActiveSlot;
            var skel = slot < 0 ? null : spine.GetSpineSkelPath(slot);
            return skel is null ? "" : PetNames.Display(skel);
        }

        /// <summary>
        /// 按字折行，逐字试宽 —— 和预览脚本同一套走法，所以「预览里几行」和「产品里几行」是同一个数。
        /// 不做标点禁则（』 可能落到行首），要改就连预览一起改，别只动一边。
        /// </summary>
        internal static string[] WrapLines(string text, Font font, int width)
        {
            using var scratch = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(scratch);
            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap };
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            var lines = new List<string>();
            var cur = "";
            foreach (var ch in text)
            {
                var trial = cur + ch;
                if (cur.Length > 0 && g.MeasureString(trial, font, new PointF(0, 0), fmt).Width > width)
                {
                    lines.Add(cur);
                    cur = ch.ToString();
                }
                else
                {
                    cur = trial;
                }
            }
            if (cur.Length > 0) lines.Add(cur);
            return lines.ToArray();
        }

        /// <summary>这条台词要占几行（判据拿真池子逐条对 MaxLines 用）</summary>
        internal static int LineCount(string text, Font font, int width) => WrapLines(text, font, width).Length;

        /// <summary>
        /// 名牌：大红底条（右端收成尖，和库里 biaoqian 名牌条同形；镜像时尖朝左）+ 烫金字。
        /// 单独抽成静态方法就是为了让 desk 判据能拿同一支笔画到一张空图上按像素量
        /// </summary>
        internal static Rectangle DrawSpeakerPlate(Graphics g, string name, Font font, Point at, bool mirror)
        {
            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap };
            var size = g.MeasureString(name, font, new PointF(at.X, at.Y), fmt);
            var box = new Rectangle(at.X, at.Y, (int)Math.Round(size.Width), (int)Math.Round(size.Height));
            if (box.Width <= 0 || box.Height <= 0) return box;

            var x0 = box.X - PlatePadX; var x1 = box.Right + PlatePadX;
            var y0 = box.Y - PlatePadY; var y1 = box.Bottom + PlatePadY;
            var ym = (y0 + y1) / 2;
            var pts = mirror
                ? new[] { new Point(x1, y0), new Point(x0 + PlateTip, y0), new Point(x0, ym),
                          new Point(x0 + PlateTip, y1), new Point(x1, y1) }
                : new[] { new Point(x0, y0), new Point(x1 - PlateTip, y0), new Point(x1, ym),
                          new Point(x1 - PlateTip, y1), new Point(x0, y1) };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                using var fill = new LinearGradientBrush(new Rectangle(x0, y0, x1 - x0, y1 - y0),
                                                         PlateTop, PlateBottom, LinearGradientMode.Vertical);
                g.FillPath(fill, path);
                using var edge = new Pen(PlateEdge, 1f);
                g.DrawPolygon(edge, pts);
            }

            // 烫金：先描一圈深金当边，再把「上亮下深」的金渐变按字压上去
            using (var outline = new SolidBrush(GoldOutline))
                foreach (var d in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) })
                    g.DrawString(name, font, outline, new PointF(at.X + d.X, at.Y + d.Y), fmt);
            using (var gold = new LinearGradientBrush(box, GoldTop, GoldBottom, LinearGradientMode.Vertical)
                   { GammaCorrection = true })
                g.DrawString(name, font, gold, new PointF(at.X, at.Y), fmt);
            return box;
        }

        private Rectangle PetRect()
        {
            var geo = Program.SpineWindow.Geometry;
            return FocusArt.PetScreenRect(new Point(geo.WindowPosition.X, geo.WindowPosition.Y),
                new Size((int)geo.WindowSize.X, (int)geo.WindowSize.Y),
                new RectangleF(geo.Box.Left, geo.Box.Top, geo.Box.Width, geo.Box.Height));
        }

        /// <summary>
        /// 画一整张：气泡、家纹按原点贴，说话人名牌、正文折行。
        /// mirror 翻美术件和各件的 x；字永远正排不翻，只是镜像那侧按行右对齐（照旧左起读，只是收拢到右边）
        /// </summary>
        private Bitmap Compose(string text, bool mirror)
        {
            var art = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format32bppPArgb);
            // 按点画就跟着画布 dpi 换算尺寸：这台 200% 的机器上整张纸被拉成两倍，右、下两道金线跑到画布外。
            // 2026-11-02 量到这笔、钉死过一版 1:1，用户看惯的是拉伸那个大小，按裁定退回拉伸 —— 别当 bug「顺手」钉回去
            using var g = Graphics.FromImage(art);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            var bubble = mirror ? assets.BubbleMirrored : assets.Bubble;
            if (bubble is not null)
                // 镜像那张是在它自己 366 宽的框里翻的，而画布左右并不等分（左留 13、右留 0）：
                // 画的位置若还按 BubbleAt 原样摆，整张纸就和按画布翻过去的正文差 13 px —— 左边那侧
                // 表现为「字贴着左框、长句压到框上」，右边那侧却看不出来（2026-11-02 用户复验抓的）
                g.DrawImage(bubble, mirror ? new Point(Mx(BubbleAt.X, bubble.Width, true), BubbleAt.Y) : BubbleAt);
            var crest = mirror ? assets.CrestMirrored : assets.Crest;
            if (crest is not null)
                g.DrawImage(crest, new Point(Mx(CrestAt.X, crest.Width, mirror), CrestAt.Y));

            using var ink = new SolidBrush(InkColour);
            if (assets.Speaker.Length > 0)
            {
                using var fmt = new StringFormat(StringFormat.GenericTypographic)
                { FormatFlags = StringFormatFlags.NoWrap };
                var w = (int)Math.Round(g.MeasureString(assets.Speaker, bodyFont,
                                                        new PointF(0, 0), fmt).Width);
                DrawSpeakerPlate(g, assets.Speaker, bodyFont,
                    new Point(Mx(NameAt.X, w, mirror), NameAt.Y), mirror);
            }

            var area = mirror
                ? new Rectangle(Mx(TextArea.X, TextArea.Width, true), TextArea.Y, TextArea.Width, TextArea.Height)
                : TextArea;
            var y = (float)area.Y;
            foreach (var line in WrapLines(text, bodyFont, area.Width).Take(MaxLines))
            {
                // 镜像那侧改成右对齐：每行按实测宽度收拢到正文区右沿，和右侧的左对齐读起来互为镜像。
                // 右侧维持用户已放行的左对齐不动（2026-11-02 拍的是「只改左侧为右对齐」）
                var x = mirror
                    ? area.Right - (float)Math.Round(g.MeasureString(line, bodyFont, new PointF(0, 0), layout).Width)
                    : (float)area.X;
                g.DrawString(line, bodyFont, ink, new PointF(x, y), layout);
                y += LineAdvance;
            }
            return art;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                assets.Dispose();
                artRight?.Dispose();
                artLeft?.Dispose();
                bodyFont.Dispose();
                layout.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
