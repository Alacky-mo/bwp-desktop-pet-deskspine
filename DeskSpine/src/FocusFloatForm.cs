using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskSpine
{
    /// <summary>
    /// 专注倒计时浮窗：贴宠物头顶的一条 mm:ss。
    /// 走 WinForms 而不是 SFML，是因为这边能用 GDI 画字贴图；宠物本体那侧照旧一个字都不画。
    /// 逐像素透明那层和预设小窗共用 <see cref="LayeredPaint"/>，素材共用 <see cref="FocusAssets"/>
    /// </summary>
    public sealed class FocusFloatForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly FocusAssets assets = FocusAssets.Load();
        private readonly Font labelFont = new(SystemFonts.MessageBoxFont!.FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly StringFormat layout = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        private Bitmap? panel;
        private string panelText = "";
        private bool onScreen;

        public FocusFloatForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;      // 一帧都看不见：整张窗由 UpdateLayeredWindow 逐像素贴
            _ = Handle;                   // 事件从别的线程转过来要先有句柄，ConfigForm 也是构造里就强建
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
        /// 每秒从番茄钟取一次数。节拍复用 ConfigForm 的 focusTimer，别再另起一只定时器 —— 两处节拍迟早漂。
        /// stowed = 宠物这会儿不方便带东西（壁纸模式沉到桌面底下、或整个藏了）
        /// </summary>
        public void OnFocusTick(TinyEngine.Pomodoro focus, bool stowed)
        {
            lock (gate) { latest = (focus, stowed); }
            Enqueue();
        }

        /// <summary>
        /// 宠物那边发的事件都到这儿：事件在渲染线程，动窗口只能回 UI 线程；
        /// 退出时句柄可能先没了，那就当这一下没发生
        /// </summary>
        public void OnGeometryChanged(TinyEngine.LayeredRenderWindow.PetGeometry _) => Enqueue();

        private void Enqueue()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { }
        }

        private (TinyEngine.Pomodoro focus, bool stowed)? latest;
        private readonly object gate = new();

        private void Apply()
        {
            TinyEngine.Pomodoro focus;
            bool stowed;
            lock (gate)
            {
                if (latest is null) return;      // 还没被番茄钟喂过数，凭空开一条没数的窗不如不开
                (focus, stowed) = latest.Value;
            }

            var phase = focus.Phase;
            var running = phase != TinyEngine.PomodoroPhase.Off;

            if (!NextVisible(running, stowed))
            {
                if (!onScreen) return;
                onScreen = false;
                LayeredPaint.Hide(Handle);
                return;
            }

            var text = $"{phase}|{focus.IsPaused}|{ClockText(focus.Remaining)}|{focus.CompletedToday}";
            if (panel is null || text != panelText)
            {
                panel?.Dispose();
                panel = Compose(focus, phase);
                panelText = text;
            }

            var anchor = Program.SpineWindow.Geometry.HeadAnchor;
            var head = new Point(anchor.X, anchor.Y);
            var bounds = FocusArt.HeadBounds(head, panel.Size, Screen.FromPoint(head).WorkingArea);
            onScreen = LayeredPaint.Push(Handle, panel, bounds);
        }

        /// <summary>
        /// 该不该显示。番茄钟在跑就露着，不再接受「点一下收起来」——
        /// 浮窗只有这一段倒计时可看，误触点没了就整段失去进度感，所以谁点都不动它。
        /// 宠物沉进壁纸层或整个藏了的时候收起来：一条 TOPMOST 的浮窗会留在最上层和它脱节
        /// </summary>
        internal static bool NextVisible(bool running, bool stowed) => running && !stowed;

        /// <summary>
        /// 秒表那一段的写法，和托盘那行同源。
        /// 不能用 <c>remaining.ToString(@"mm\:ss")</c>：那是「分」这一位（0..59），
        /// 设到 60 分钟以上会读成 00:00，而时长上限给到 180
        /// </summary>
        internal static string ClockText(TimeSpan remaining)
            => $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";

        /// <summary>
        /// 画一整张带透明的图：底是 #2b2b2b 的圆角板，边上贴 bg_kuang02 九宫格拉出来的白框，
        /// 内容是「段名 + mm:ss」，数字一张图一个字
        /// </summary>
        private Bitmap Compose(TinyEngine.Pomodoro focus, TinyEngine.PomodoroPhase phase)
        {
            var clock = ClockText(focus.Remaining);
            var label = focus.IsPaused ? "已暂停" : ConfigForm.FocusPhaseName(phase);

            // 素材没装机（衍生图不进版本库，换台机器就是空的）就整行退回系统字体，别画出一半缺一半
            var strip = assets.FloatFrame is null ? null : FocusArt.DigitStrip(clock, assets.Digits);
            var line = strip is null ? $"{label}  {clock}" : label;

            Size textSize, content, size;
            using (var scratch = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
            using (var probe = Graphics.FromImage(scratch))
            {
                probe.TextRenderingHint = TextRenderingHint.AntiAlias;
                textSize = probe.MeasureString(line, labelFont, new PointF(0, 0), layout).ToSize();
                content = new Size(textSize.Width + (strip is null ? 0 : FocusArt.DigitGap + strip.Width),
                                   Math.Max(textSize.Height, strip?.Height ?? 0));
                size = FocusArt.PanelSize(content.Width, content.Height);
            }

            var art = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(art);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            FocusArt.Crisp(g);

            var box = new Rectangle(0, 0, size.Width, size.Height);
            using (var path = FocusArt.RoundedBox(box, FocusArt.CornerRadius))
            using (var back = new SolidBrush(FocusArt.BackColour))
                g.FillPath(back, path);
            if (assets.FloatFrame is not null) FocusArt.NineSlice(g, assets.FloatFrame, box);

            var top = (content.Height - textSize.Height) / 2;
            using (var ink = new SolidBrush(FocusArt.InkColour))
                g.DrawString(line, labelFont, ink, new PointF(FocusArt.PadX, top), layout);
            if (strip is not null)
            {
                g.DrawImage(strip, new Rectangle(FocusArt.PadX + textSize.Width + FocusArt.DigitGap,
                                  (content.Height - strip.Height) / 2, strip.Width, strip.Height));
                strip.Dispose();
            }
            return art;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                assets.Dispose();
                panel?.Dispose();
                labelFont.Dispose();
                layout.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
