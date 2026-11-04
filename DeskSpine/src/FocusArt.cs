using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace DeskSpine
{
    /// <summary>
    /// 倒计时浮窗那一层的画法：九宫格、数字条、贴头顶的矩形。
    /// 全是纯函数、不碰窗口，所以无头判据能直接喂数对结果；
    /// 「贴得正不正、字看得清不清」还是得用户本人看桌面，抓屏在这儿帮不上忙
    /// </summary>
    internal static class FocusArt
    {
        /// <summary>
        /// bg_kuang02 要保的边带宽：圆角起点 3px、那道亮边到 4px，inset 取 4 两头才都盖住
        /// （量出来的数在 bwp-assets/out/nine_patch/结果.tsv）
        /// </summary>
        public const int FrameInset = 4;

        /// <summary>
        /// bg_tips_3 要保的边带宽：顶栏那两笔墨痕加粉光占 y=17~32（量自装机那张图），
        /// 边带窄于此就被当成可拉伸中段竖着拉长 —— 16 的时候标题旁边就是两坨 smear。
        /// 33 还顺带把左下角那截卷角整块留在角上（它从 y=199 起），纸自己的左边线（第 20 列）也落在边带里不会被拉断
        /// </summary>
        public const int PanelInset = 33;

        /// <summary>数字条的成品高。十个字本来差 15%，不归一的话「25:09」是一排锯齿；配平在素材侧做，见 DigitStrip</summary>
        public const int DigitHeight = 36;

        /// <summary>数字条和段名之间的缝</summary>
        public const int DigitGap = 3;

        /// <summary>bg_kuang02 的圆角起点。九宫格只拉中间，这个数拉到多大都不变</summary>
        public const int CornerRadius = 3;

        /// <summary>字色：和数字同一套暖族的实心米白 #fefbee</summary>
        public static readonly Color InkColour = Color.FromArgb(254, 251, 238);

        /// <summary>数字条到框内侧的留白</summary>
        public const int PadX = 14;
        public const int PadY = 9;

        /// <summary>浮窗底边离头顶锚点的距离</summary>
        public const int HeadGap = 6;

        /// <summary>
        /// 底板色。库里没有「深色又不透明」的面板（§3.4 第 3 条），
        /// 所以自己铺一层 #2b2b2b，再把 bg_kuang02 那道白框贴上去当边
        /// </summary>
        public static readonly Color BackColour = Color.FromArgb(240, 43, 43, 43);

        /// <summary>
        /// 三段切九块贴：四角原样、上下边只横着拉、左右边只竖着拉、中间双向拉。
        /// 这就是「28px 的框拉成 420x150 而圆角还是 3px」的全部机关，整图缩放做不到
        /// </summary>
        public static void NineSlice(Graphics g, Bitmap tile, Rectangle dest, int inset = FrameInset)
        {
            // inset 顶到图的一半就没有中间可拉了，钳住；框再小也只是角叠角，不会画到画布外
            inset = Math.Max(1, Math.Min(inset, Math.Min(tile.Width, tile.Height) / 2));
            var w = dest.Width;
            var h = dest.Height;
            var iw = tile.Width;
            var ih = tile.Height;

            Piece(g, tile, dest.X, dest.Y, inset, inset, 0, 0, inset, inset);
            Piece(g, tile, dest.X + w - inset, dest.Y, inset, inset, iw - inset, 0, inset, inset);
            Piece(g, tile, dest.X, dest.Y + h - inset, inset, inset, 0, ih - inset, inset, inset);
            Piece(g, tile, dest.X + w - inset, dest.Y + h - inset, inset, inset, iw - inset, ih - inset, inset, inset);

            Piece(g, tile, dest.X + inset, dest.Y, w - 2 * inset, inset, inset, 0, iw - 2 * inset, inset);
            Piece(g, tile, dest.X + inset, dest.Y + h - inset, w - 2 * inset, inset, inset, ih - inset, iw - 2 * inset, inset);
            Piece(g, tile, dest.X, dest.Y + inset, inset, h - 2 * inset, 0, inset, inset, ih - 2 * inset);
            Piece(g, tile, dest.X + w - inset, dest.Y + inset, inset, h - 2 * inset, iw - inset, inset, inset, ih - 2 * inset);
            Piece(g, tile, dest.X + inset, dest.Y + inset, w - 2 * inset, h - 2 * inset, inset, inset, iw - 2 * inset, ih - 2 * inset);
        }

        private static void Piece(Graphics g, Bitmap tile, int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh)
        {
            if (dw <= 0 || dh <= 0 || sw <= 0 || sh <= 0) return;
            g.DrawImage(tile, new Rectangle(dx, dy, dw, dh), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
        }

        /// <summary>
        /// 把「25:09」拼成一张透明底的横条：一张图一个字，整图缩到 height 高。
        /// 齐不齐是素材的事 —— res/image/focus_ui/digits 每张都坐在同一张 100 高画布、同一条基线上
        /// （配平脚本 bwp-assets/out/focus_digits/make_balanced.py），所以这里等比缩就是共基线。
        /// 缺任何一个字就返回 null —— 调用方退回系统字体，别画出一半缺一半
        /// </summary>
        public static Bitmap? DigitStrip(string text, IReadOnlyDictionary<char, Bitmap> tiles,
            int height = DigitHeight, int gap = DigitGap)
        {
            var widths = new List<int>();
            foreach (var ch in text)
            {
                if (ch == ' ')
                {
                    widths.Add(height / 2);
                    continue;
                }
                if (!tiles.TryGetValue(ch, out var tile) || tile.Height <= 0) return null;
                widths.Add(Math.Max(1, (int)Math.Round(tile.Width * (height / (double)tile.Height))));
            }
            if (widths.Count == 0) return null;

            var total = widths.Sum() + gap * (widths.Count - 1);
            var strip = new Bitmap(total, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.Transparent);
                Crisp(g);
                var x = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != ' ')
                        g.DrawImage(tiles[text[i]], new Rectangle(x, 0, widths[i], height));
                    x += widths[i] + gap;
                }
            }
            return strip;
        }

        /// <summary>
        /// 头顶锚点（屏幕坐标，框上沿中点）→ 浮窗该摆的矩形。
        /// 水平居中、上沿留 gap，越出工作区就顺着边挪 —— 宁可压到宠物身上也别跑到屏幕外
        /// </summary>
        public static Rectangle HeadBounds(Point head, Size size, Rectangle workArea, int gap = HeadGap)
        {
            var x = head.X - size.Width / 2;
            var y = head.Y - gap - size.Height;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - size.Width));
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - size.Height));
            return new Rectangle(x, y, size.Width, size.Height);
        }

        /// <summary>内容多大，框就多大（四周各留 PadX/PadY）</summary>
        public static Size PanelSize(int contentWidth, int contentHeight)
            => new(contentWidth + 2 * PadX, contentHeight + 2 * PadY);

        /// <summary>
        /// 小窗摆在宠物边上：贴着宠物框的右沿、顶对齐；右边放不下就翻到左边，两头都挤就夹回工作区。
        /// 和浮窗同一套「跟着宠物」的道理，只是小窗要摆在旁边而不是头上，别把倒计时条盖住
        /// </summary>
        public static Rectangle SideBounds(Rectangle pet, Size panel, Rectangle workArea, int gap = 8)
        {
            var x = pet.Right + gap;
            if (x + panel.Width > workArea.Right) x = pet.Left - gap - panel.Width;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - panel.Width));
            var y = Math.Max(workArea.Top, Math.Min(pet.Top, workArea.Bottom - panel.Height));
            return new Rectangle(x, y, panel.Width, panel.Height);
        }

        /// <summary>
        /// 宠物本体占的那块屏幕地面。骨骼包围盒是窗口内的像素，加上窗口左上角才落到屏幕上。
        /// 包围盒是空的（骨骼还没装载）就退成头顶那一个点 —— 拿整扇窗口矩形去摆位，
        /// 窗口本来就铺满屏幕，SideBounds 左右都放不下，小窗只会被夹到屏幕左上角
        /// </summary>
        public static Rectangle PetScreenRect(Point windowPos, Size windowSize, RectangleF box)
            => box.Width > 0f && box.Height > 0f
                ? new Rectangle(windowPos.X + (int)Math.Round(box.Left), windowPos.Y + (int)Math.Round(box.Top),
                                Math.Max(1, (int)Math.Round(box.Width)), Math.Max(1, (int)Math.Round(box.Height)))
                : new Rectangle(windowPos.X + windowSize.Width / 2, windowPos.Y, 0, 0);

        /// <summary>圆角板：浮窗的底和小窗里垫数字的那条深色内板共用</summary>
        public static GraphicsPath RoundedBox(Rectangle box, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(box.Left, box.Top, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 拉伸按最近邻：三 px 的白边拉长了还是三 px，换成双线性就糊成一片奶白
        /// </summary>
        public static void Crisp(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
        }
    }
}
