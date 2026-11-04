using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskSpine
{
    /// <summary>
    /// 右键宠物弹的那张功能选择小窗：两行「番茄钟」「查看天气」，挑一行就开那一扇，它自己不装任何功能。
    /// 和预设小窗同一个画法 —— 一张 GDI 合成的图贴成分层窗，WinForms 控件一个都不用，所以风格跟着库里的素材走。
    /// 排版、命中、开哪扇全是纯函数，无头判据能直接喂坐标对表；「挤不挤、点得准不准」还是得用户本人看桌面
    /// </summary>
    public sealed class PetMenuForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>一行等于一扇门。加功能就是往 <see cref="Items"/> 里添一行，这里跟着长一格</summary>
        internal enum Door { Pomodoro, Weather }

        /// <summary>
        /// 这张小窗就是这张表：画的时候按它排、命中的时候按它认、点下去按它开 —— 三处共用一份，
        /// 「第二行开成了番茄钟」这种错位就没地方藏（desk 第 23 条逐行对文字和去向）
        /// </summary>
        internal static readonly (Door Where, string Label)[] Items =
        {
            (Door.Pomodoro, "番茄钟"),
            (Door.Weather, "查看天气"),
        };

        internal enum Hit { None, Close, Row }

        /// <summary>画和打靶共用的一份矩形。Rows 和 Items 一行对一行</summary>
        internal readonly record struct Spots(Rectangle Panel, Rectangle Title, Rectangle Close, Rectangle[] Rows);

        private const int PadX = 26;
        private const int PadY = 12;
        private const int TitleY = 24;        // 和预设小窗同一张纸：顶栏那两笔墨痕占 y=17~28，标题坐 PadY 就会被压着
        private const int Gap = 8;
        private const int TitleH = 44;
        private const int ButtonW = 150;      // com_btn_press 的原尺寸，一格都不缩
        private const int ButtonH = 66;
        private const int CloseSize = 44;     // com_btn_close 的原尺寸
        private const int NativePanelWidth = 276;     // bg_tips_3 的原始宽度

        /// <summary>纸面左边线到面板左沿的距离，照抄预设小窗那个数（同一张纸，量两遍迟早对不上，改的时候两处一起改）</summary>
        private const int PaperMargin = 20;

        private static readonly Color PaperInk = Color.FromArgb(43, 43, 43);

        private readonly FocusAssets assets = FocusAssets.Load();
        private readonly Font titleFont = new(FocusPresetForm.TitleFamily, FocusPresetForm.TitleSize,
            FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly StringFormat leftText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        private readonly StringFormat centreText = new(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        private Spots spots = Arrange(NativePanelWidth);
        private Bitmap? panel;
        private string stateText = "";
        private (Hit hit, int index) pressed;
        private bool onScreen;
        private bool wanted;
        private readonly object gate = new();

        public PetMenuForm()
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
        /// 右键那一下：开在宠物边上，顺带把占着这块地的两张板收回去（菜单要开在它们那块纸上，留着就三张叠一张）。
        /// 任何线程都能叫
        /// </summary>
        public void OpenAtPet()
        {
            Program.FocusPreset?.Yield();
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
        /// 现在想不想开着。台词气泡问这个 —— 它和预设小窗抢的都是宠物右侧同一块地方，
        /// 菜单开着时气泡让位（菜单自己不开气泡那一扇，所以这里只看 wanted）
        /// </summary>
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

            if (panel is null || stateText != $"p{pressed.hit}{pressed.index}")
            {
                panel?.Dispose();
                spots = Arrange(assets.Panel?.Width ?? NativePanelWidth);
                panel = Compose(spots);
                stateText = $"p{pressed.hit}{pressed.index}";
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
        /// 纵向数两行, 横向把按钮摆在纸面中线上。宽度用素材原宽, 所以九宫格其实只补纵向那一截。
        /// 高度不跟着两行缩 —— 用户 2026-11-03 拍的是三扇小框整体长度一致, 两行数不满就底下留空白
        /// </summary>
        internal static Spots Arrange(int panelWidth)
        {
            var rows = new Rectangle[Items.Length];
            var y = TitleY + TitleH + Gap;
            for (int i = 0; i < Items.Length; i++)
            {
                rows[i] = new Rectangle(CentreX(panelWidth, ButtonW), y, ButtonW, ButtonH);
                y += ButtonH + Gap;
            }
            var close = new Rectangle(panelWidth - PadY - CloseSize, PadY, CloseSize, CloseSize);
            return new Spots(new Rectangle(0, 0, panelWidth, Math.Max(y - Gap + PadY, FocusPresetForm.PanelHeight(panelWidth))),
                new Rectangle(PadX, TitleY, close.X - Gap - PadX, TitleH), close, rows);
        }

        /// <summary>横向中线取纸面那一半：左边那 20 px 是抹完碎片留下的空白，照整幅宽居会把内容顶到纸边上去</summary>
        private static int CentreX(int panelWidth, int width) => Math.Max(PadX, (panelWidth + PaperMargin - width) / 2);

        internal static (Hit hit, int index) HitTest(Spots spots, Point at)
        {
            if (spots.Close.Contains(at)) return (Hit.Close, -1);
            for (int i = 0; i < spots.Rows.Length; i++)
                if (spots.Rows[i].Contains(at)) return (Hit.Row, i);
            return (Hit.None, -1);
        }

        /// <summary>整张小窗画成一张图：底板 bg_tips_3 九宫格，两行都是 com_btn_press 原尺寸，字压在按钮正中</summary>
        private Bitmap Compose(Spots s)
        {
            var art = new Bitmap(s.Panel.Width, s.Panel.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(art);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            FocusArt.Crisp(g);

            // 素材没装机就整块退回深色圆角板, 字跟着换成米白 —— 别浅底配浅字
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

                g.DrawString("功能", titleFont, ink, s.Title, leftText);
                DrawCentered(g, assets.Close, s.Close);

                for (int i = 0; i < s.Rows.Length; i++)
                {
                    var row = s.Rows[i];
                    var button = pressed is (Hit.Row, var which) && which == i ? assets.ButtonPressed : assets.ButtonNormal;
                    DrawCentered(g, button, row);
                    g.DrawString(Items[i].Label, bodyFont, cream, row, centreText);
                }
            }
            return art;
        }

        /// <summary>素材缺就不画这一块, 但位置留着 —— 少一张图不该把整块面板挪位</summary>
        private static void DrawCentered(Graphics g, Bitmap? tile, Rectangle box)
        {
            if (tile is null) return;
            g.DrawImage(tile, new Rectangle(box.X + (box.Width - tile.Width) / 2,
                                           box.Y + (box.Height - tile.Height) / 2, tile.Width, tile.Height));
        }

        /// <summary>
        /// 收起自己再开门。两张板摆的是宠物右侧同一块地方, 不先收起就会叠出花来;
        /// 菜单这一侧关掉是应当的 —— 它只是过道
        /// </summary>
        private void Act(int index)
        {
            lock (gate) { wanted = false; }
            Stow();
            switch (Items[index].Where)
            {
                case Door.Pomodoro: Program.FocusPreset.OpenAtPet(); break;
                case Door.Weather: Program.Weather.OpenAtPet(); break;
            }
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
            if (at != was || at.hit is Hit.None) { Enqueue(); return; }   // 按下和抬起不在同一块, 就不算一次点
            if (at.hit is Hit.Row) Act(at.index);
            else
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
                titleFont.Dispose();
                bodyFont.Dispose();
                leftText.Dispose();
                centreText.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
