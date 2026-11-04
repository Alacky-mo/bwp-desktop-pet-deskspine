using System;

namespace TinyEngine
{
    /// <summary>
    /// 从一张带 alpha 的图上量剪影。头像框要照游戏那样把洞开在脸上, 而脸只认得出来的画出来的像素:
    /// 骨骼包围盒会被图集区域里的透明留白撑大 (不知火实测大 13%, 而且全撑在下半身),
    /// 所以「画出来的方框」和「头团中心」都单独量, 不拿包围盒中心当脸。
    /// </summary>
    public static class Silhouette
    {
        /// <summary>
        /// 画出来的那部分的外接框, 图坐标 (左上角为原点, Y 向下)。整张全透明返回 null
        /// </summary>
        public static SFML.Graphics.FloatRect? OpaqueBox(SFML.Graphics.Image image, int alphaMin = 8)
        {
            if (!TryPixels(image, out int w, out int h, out var px)) return null;
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (px[(y * w + x) * 4 + 3] <= alphaMin) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            return x1 < x0 ? null : new SFML.Graphics.FloatRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        /// <summary>
        /// 头团 = 剪影上半截里最厚的那块圆盘。两遍 chamfer 距离变换 (权重 10/14, 除以 10 就是像素半径),
        /// 整数近似够定位用。返回圆心和半径 (图坐标), 全透明返回 null。
        ///
        /// 不能取全图最大圆: Q 版半身像的胸口、裙甲常常比头还厚一点 (千姬实测胸口半径 30、头 28),
        /// 最大圆会量到胸口, 环就滑到下巴底下。游戏的洞开在脸上, 而脸总在画出来那截的上半截里,
        /// 所以只在「画出来的方框」上面 HeadBand 的范围内挑最厚的圆盘
        /// </summary>
        public static (float X, float Y, float R)? HeadMass(SFML.Graphics.Image image, int alphaMin = 8)
        {
            if (!TryPixels(image, out int n, out int h, out var px)) return null;
            if (image.Size.X != (uint)n) return null;   // 距离变换按正方形图写的, 非正方形不量
            var painted = OpaqueBox(image, alphaMin);
            if (painted is not { } pb) return null;

            var d = new int[n * n];
            bool Solid(int x, int y) => px[(y * n + x) * 4 + 3] > alphaMin;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    d[y * n + x] = Solid(x, y) ? 9 * n : 0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    if (d[i] == 0) continue;
                    if (x > 0) d[i] = Math.Min(d[i], d[i - 1] + 10);
                    if (y > 0) d[i] = Math.Min(d[i], d[i - n] + 10);
                    if (x > 0 && y > 0) d[i] = Math.Min(d[i], d[i - n - 1] + 14);
                    if (x < n - 1 && y > 0) d[i] = Math.Min(d[i], d[i - n + 1] + 14);
                }
            // 反向那遍只会把值改小, 所以峰值要等它跑完再找
            for (int y = n - 1; y >= 0; y--)
                for (int x = n - 1; x >= 0; x--)
                {
                    int i = y * n + x;
                    if (d[i] == 0) continue;
                    if (x < n - 1) d[i] = Math.Min(d[i], d[i + 1] + 10);
                    if (y < n - 1) d[i] = Math.Min(d[i], d[i + n] + 10);
                    if (x < n - 1 && y < n - 1) d[i] = Math.Min(d[i], d[i + n + 1] + 14);
                    if (x > 0 && y < n - 1) d[i] = Math.Min(d[i], d[i + n - 1] + 14);
                }

            int limit = Math.Min(n, (int)(pb.Top + pb.Height * HeadBand));
            int best = 0, bi = -1;
            for (int y = 0; y < limit; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    if (d[i] > best) { best = d[i]; bi = i; }
                }
            return bi < 0 ? null : (bi % n, bi / n, best / 10f);
        }

        /// <summary>脸所在的范围占「画出来的那截」高度的比例, 再往下的都当前身子排掉</summary>
        private const float HeadBand = 0.6f;

        static bool TryPixels(SFML.Graphics.Image image, out int w, out int h, out byte[] px)
        {
            w = (int)image.Size.X;
            h = (int)image.Size.Y;
            px = image.Pixels;      // RGBA 字节流, 每像素 4 个字节, alpha 是第 4 个
            return w >= 8 && h >= 8 && px.Length >= w * h * 4;
        }
    }
}
