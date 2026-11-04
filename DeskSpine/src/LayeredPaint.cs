using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskSpine
{
    /// <summary>
    /// 逐像素透明的那层贴法，倒计时浮窗和预设小窗共用一张画法。
    /// 走 UpdateLayeredWindow 而不是 WinForms 的 Opacity / TransparencyKey：
    /// 这两张图都是「实心板 + 圆角外透明」，颜色键模式做不到，和 ULW 混用行为没定义
    /// </summary>
    internal static class LayeredPaint
    {
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;
        private const uint ULW_ALPHA = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;

        /// <summary>
        /// 位置、大小、像素一次给完，所以不需要 WinForms 再去动 Bounds。
        /// 图必须是 Format32bppPArgb：GetHbitmap 出来的就是 ULW 要的 BGRA 预乘
        /// </summary>
        public static bool Push(IntPtr hwnd, Bitmap art, Rectangle bounds)
        {
            var screenDc = GetDC(hwnd);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = art.GetHbitmap(Color.FromArgb(0));
            var oldBitmap = SelectObject(memDc, hBitmap);
            try
            {
                var position = new POINT { X = bounds.X, Y = bounds.Y };
                var size = new SIZE { cx = bounds.Width, cy = bounds.Height };
                var source = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                // Win32 那三个尾巴的顺序是 crKey, pblend, dwFlags，声明里写反过就等于递过去一个空指针，ULW 永远返回 false
                var done = UpdateLayeredWindow(hwnd, screenDc, ref position, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
                // 别把用户正在打字的那个窗口挤下去
                if (done) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                return done;
            }
            finally
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(hwnd, screenDc);
            }
        }

        public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pbf, uint dwFlags);

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    }
}
