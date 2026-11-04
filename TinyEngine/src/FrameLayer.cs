using Spine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TinyEngine
{
    /// <summary>
    /// 头像框图层: 把游戏里的圆环头像框贴在出场骨骼的头上。
    /// 环画布一比一顶替剪影包围盒, 贴合靠量出来的洞心而不是手调系数, 换角色换窗口大小都不用重新调
    /// </summary>
    public class FrameLayer : Renderable, IDisposable
    {
        private readonly SFML.Graphics.Texture?[] frames;
        private readonly string?[] framePaths;
        private readonly RingGeometry?[] geometry;
        private readonly SFML.Graphics.Sprite sprite = new();

        /// <summary>
        /// 头像框名册, 能装几个框由槽位数决定
        /// </summary>
        public FrameLayer(int frameCount)
        {
            frames = new SFML.Graphics.Texture?[frameCount];
            framePaths = new string?[frameCount];
            geometry = new RingGeometry?[frameCount];
        }

        ~FrameLayer() { Dispose(false); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i]?.Dispose();
                frames[i] = null;
            }
            if (disposing)
            {
                probe?.Dispose();
                probe = null;
            }
        }

        /// <summary>
        /// 装载头像框到指定槽位, 路径没变就什么都不做
        /// </summary>
        public void SetFrame(string? path, int index)
        {
            if (index >= frames.Length)
                throw new ArgumentOutOfRangeException($"Max frame slot count: {frames.Length}, got index {index}");

            if (path == framePaths[index])
                return;

            if (string.IsNullOrEmpty(path))
            {
                frames[index]?.Dispose();
                frames[index] = null;
                framePaths[index] = null;
                geometry[index] = null;
                Debug.WriteLine($"Unload frame[{index}]");
                if (index == activeFrame) ActiveFrame = index;
                return;
            }

            Debug.WriteLine($"Loading frame[{index}] from {path}");
            var texture = new SFML.Graphics.Texture(path);
            texture.Smooth = true;
            frames[index]?.Dispose();
            frames[index] = texture;
            framePaths[index] = path;
            using (var image = texture.CopyToImage())
                geometry[index] = MeasureHole(image);
            if (geometry[index] is null)
                Debug.WriteLine($"frame[{index}] 量不出放头像的洞, 只能退回按图心贴合");
            fitted = false;
        }

        /// <summary>
        /// 取指定槽位的头像框路径
        /// </summary>
        public string? GetFramePath(int index)
        {
            if (index >= frames.Length)
                throw new ArgumentOutOfRangeException($"Max frame slot count: {frames.Length}, got index {index}");
            return framePaths[index];
        }

        /// <summary>
        /// 已装载头像框的槽位下标
        /// </summary>
        public IEnumerable<int> LoadedFrames => Enumerable.Range(0, frames.Length).Where(i => frames[i] is not null);

        /// <summary>
        /// 出场的头像框, -1 = 不画
        /// </summary>
        public int ActiveFrame
        {
            get => activeFrame;
            set
            {
                var target = value >= 0 && (value >= frames.Length || frames[value] is null)
                    ? Array.FindIndex(frames, f => f is not null)
                    : value;
                if (activeFrame == target) return;
                activeFrame = target;
                fitted = false;
            }
        }
        private int activeFrame = -1;

        /// <summary>
        /// 跟着哪只骨骼; 包围盒每帧从它身上取
        /// </summary>
        public SpineSlot? Follow { get; set; }

        /// <summary>
        /// 一张框量出来的几何。洞心用纹理坐标 (从图左上角往右往下), 装载时算一次
        /// </summary>
        public readonly struct RingGeometry
        {
            public RingGeometry(SFML.System.Vector2f holeCentre, float holeDiameter, float canvasSide)
            {
                HoleCentre = holeCentre; HoleDiameter = holeDiameter; CanvasSide = canvasSide;
            }
            public SFML.System.Vector2f HoleCentre { get; }
            public float HoleDiameter { get; }
            public float CanvasSide { get; }
            /// <summary>洞径占画布的比例。游戏这十张实测 0.52~0.57</summary>
            public float HoleOverCanvas => CanvasSide > 0f ? HoleDiameter / CanvasSide : 0f;
        }

        /// <summary>
        /// 指定槽位的框几何, 没装载或量不出洞就是 null
        /// </summary>
        public RingGeometry? GetGeometry(int index) =>
            index >= 0 && index < geometry.Length ? geometry[index] : null;

        /// <summary>
        /// 当前这一帧环实际占的方框 (平滑后的)
        /// </summary>
        public SFML.Graphics.FloatRect CurrentBox => box;

        /// <summary>
        /// 环的画布边长占「画出来的那截身子」高度的比例。游戏把十张框全画在 128x128 的画布里,
        /// 放头像的洞就落在画布正中 (实测洞心离画布中心 ≤5px), 洞径占画布 0.52~0.57;
        /// 桌宠的剪影方框正好顶替游戏那个头像方框, 所以一比一, 不再手调直径。
        /// </summary>
        public float BoxOverHead { get; set; } = 1f;

        /// <summary>
        /// 锚点的微调偏移, 世界坐标 (屏幕上 Y 越大越靠上)
        /// </summary>
        public SFML.System.Vector2f Offset { get; set; } = new(0, 0);

        /// <summary>
        /// 贴合速度的倒数, 越大环跟得越紧。平滑是为了不让环随着每帧包围盒的抖动一起抖
        /// </summary>
        public float FollowSpeed { get; set; } = 6f;

        private SFML.Graphics.FloatRect box;
        private bool fitted = false;

        /// <summary>
        /// 离屏量剪影用的探针图, 按 ProbeEvery 的节拍画一发量头在哪
        /// </summary>
        private SFML.Graphics.RenderTexture? probe;
        /// <summary>头团心相对包围盒中心的世界偏移, 量不到就是零</summary>
        private SFML.System.Vector2f headShift;
        /// <summary>画出来的高度占包围盒高度的比例, 量不到就按一比一</summary>
        private float paintedOverBounds = 1f;
        private int probedSlot = int.MinValue;
        private float probeWait;

        /// <summary>
        /// 隔多久重量一次头在哪。头在待机里两秒才晃一圈, 十赫兹足够跟住, 再密只是白烧 CPU
        /// (探针一发要读回 256x256 并跑三遍距离变换, 每帧量会让桌宠的帧预算多吃两成)
        /// </summary>
        public float ProbeEvery { get; set; } = 0.1f;

        private const uint ProbeSide = 256;

        /// <summary>
        /// 最近一次探针量到的东西: 头团心相对包围盒中心的世界偏移 + 画出来的高度占包围盒的比例。
        /// 量不到时是 (0, 0, 1), 也就是退回按包围盒中心一比一贴合
        /// </summary>
        public (float ShiftX, float ShiftY, float PaintedOver) ProbeInfo => (headShift.X, headShift.Y, paintedOverBounds);

        /// <summary>
        /// 离屏拍一张, 量出「画出来的方框」和头团心, 量到就直接用。
        /// 骨骼包围盒会把图集区域里的透明留白也算进去, 于是环既偏大又往下坠, 所以这两样必须实测;
        /// 呼吸和换表情也会挪头, 所以要续量 (节拍见 ProbeEvery), 抖由 CurrentBox 的平滑去吃掉
        /// </summary>
        private void Probe()
        {
            var follow = Follow;
            probeWait = 0;
            var bounds = follow?.ActiveWorldBounds ?? default;
            if (follow is null || bounds.Width <= 0f || bounds.Height <= 0f) return;

            var centre = new SFML.System.Vector2f(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
            // 视图把包围盒整个框进来再留两成余量, 剪影贴着图边就量不全了
            var side = Math.Max(bounds.Width, bounds.Height) * 1.2f;
            var worldPerPixel = side / ProbeSide;

            probe ??= new SFML.Graphics.RenderTexture(ProbeSide, ProbeSide, new SFML.Window.ContextSettings());
            probe.SetView(new SFML.Graphics.View(centre, new SFML.System.Vector2f(side, -side)));
            probe.Clear(new SFML.Graphics.Color(0, 0, 0, 0));
            follow.Render(probe);
            probe.Display();

            using var image = probe.Texture.CopyToImage();
            // 这一发量不到就沿用上一发的数, 别把环甩回包围盒中心
            var painted = Silhouette.OpaqueBox(image);
            if (painted is { } p)
                paintedOverBounds = p.Height * worldPerPixel / bounds.Height;
            var head = Silhouette.HeadMass(image);
            if (head is { } h)
                // 探针视图的 Y 是翻的, 所以像素往下 = 世界往小
                headShift = new SFML.System.Vector2f(
                    (h.X - ProbeSide / 2f) * worldPerPixel,
                    -(h.Y - ProbeSide / 2f) * worldPerPixel);
        }

        /// <summary>
        /// 按当前姿态算出环该待的方框。骨骼还没装载就是 null
        /// </summary>
        private SFML.Graphics.FloatRect? TargetBox()
        {
            var bounds = Follow?.ActiveWorldBounds ?? default;
            if (bounds.Width <= 0f || bounds.Height <= 0f) return null;

            var d = bounds.Height * paintedOverBounds * BoxOverHead;
            // 游戏里环画布和头像方框是同一个方, 所以拿剪影方框一比一顶上;
            // 落点跟着头团心, 因为游戏那个洞是开在脸上的, 不是开在方框正中
            var cx = bounds.Left + bounds.Width * 0.5f + headShift.X + Offset.X;
            var cy = bounds.Top + bounds.Height * 0.5f + headShift.Y + Offset.Y;
            return new(cx - d * 0.5f, cy - d * 0.5f, d, d);
        }

        public override void Update(float delta)
        {
            base.Update(delta);

            // 没框戴就整个跳过: 探针一发要离屏渲染整只宠物再跑三遍距离变换,
            // 环不画的时候这些纯属白烧 (ActiveFrame 的取值保证 -1 就是名册全空)
            if (activeFrame < 0) return;

            var slot = Follow?.ActiveSlot ?? -1;
            if (slot != probedSlot)
            {
                // 换过角色了: 上一只量出来的头心不作数, 环也要直接落位, 不要从上一只的位置飘过来
                probedSlot = slot;
                headShift = default;
                paintedOverBounds = 1f;
                probeWait = float.MaxValue;
                fitted = false;
            }
            if ((probeWait += delta) >= ProbeEvery) Probe();

            var target = TargetBox();
            if (target is null)
            {
                fitted = false;
                return;
            }

            // 换角色/换框/刚装载时直接落位, 不要从上一只的位置慢慢飘过来
            if (!fitted)
            {
                box = target.Value;
                fitted = true;
                return;
            }

            var t = 1f - MathF.Exp(-delta * FollowSpeed);
            box = new(
                box.Left + (target.Value.Left - box.Left) * t,
                box.Top + (target.Value.Top - box.Top) * t,
                box.Width + (target.Value.Width - box.Width) * t,
                box.Height + (target.Value.Height - box.Height) * t);
        }

        public override void Render(SFML.Graphics.RenderTarget target)
        {
            base.Render(target);

            var texture = activeFrame >= 0 && activeFrame < frames.Length ? frames[activeFrame] : null;
            if (texture is null || box.Width <= 0f) return;

            // 渲染视图的 Y 是翻的, 所以纵向缩放取负: 图上边落到世界坐标的大 Y, 环才不会上下颠倒
            sprite.Texture = texture;
            // Origin 取洞心而不是图心: 框画在画布里并不居中 (装饰件顶到哪裁到哪),
            // 拿洞心当锚点, Position 落在包围盒中心时压住头的才是洞
            sprite.Origin = geometry[activeFrame]?.HoleCentre
                ?? new SFML.System.Vector2f(texture.Size.X / 2f, texture.Size.Y / 2f);
            sprite.Scale = new SFML.System.Vector2f(box.Width / texture.Size.X, -box.Height / texture.Size.Y);
            sprite.Position = new SFML.System.Vector2f(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
            target.Draw(sprite);
        }

        /// <summary>
        /// 从框的 alpha 里量出「放头像的那个洞」: 先排掉和实心外接框边连成一片的透明 (那是框外),
        /// 剩下的空洞里取最大的一块, 用它的外接方框中心当洞心。
        /// 只在实心外接框里做这件事: 浪花、花枝这些装饰件常常顶到图边,
        /// 在整张图上量洞会和框外通成一片, 判据当场选出画布角。
        /// </summary>
        private static RingGeometry? MeasureHole(SFML.Graphics.Image image)
        {
            int w = (int)image.Size.X, h = (int)image.Size.Y;
            if (w < 8 || h < 8) return null;
            var pixels = image.Pixels;      // RGBA 字节流, 每像素 4 个字节, alpha 是第 4 个
            if (pixels.Length < w * h * 4) return null;
            var solid = new bool[w * h];
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (pixels[(y * w + x) * 4 + 3] <= 8) continue;
                    solid[y * w + x] = true;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            if (x1 < x0 || y1 < y0) return null;

            var outside = new bool[w * h];
            var flood = new Stack<int>();
            void Push(int x, int y)
            {
                int i = y * w + x;
                if (solid[i] || outside[i]) return;
                outside[i] = true;
                flood.Push(i);
            }
            for (int x = x0; x <= x1; x++) { Push(x, y0); Push(x, y1); }
            for (int y = y0; y <= y1; y++) { Push(x0, y); Push(x1, y); }
            while (flood.Count > 0)
            {
                int i = flood.Pop(), x = i % w, y = i / w;
                if (x > x0) Push(x - 1, y);
                if (x < x1) Push(x + 1, y);
                if (y > y0) Push(x, y - 1);
                if (y < y1) Push(x, y + 1);
            }

            var seen = new bool[w * h];
            var blob = new Stack<int>();
            int bestN = 0, bestX0 = 0, bestX1 = 0, bestY0 = 0, bestY1 = 0;
            for (int sy = y0; sy <= y1; sy++)
                for (int sx = x0; sx <= x1; sx++)
                {
                    int start = sy * w + sx;
                    if (solid[start] || outside[start] || seen[start]) continue;
                    int n = 0, bx0 = sx, bx1 = sx, by0 = sy, by1 = sy;
                    seen[start] = true;
                    blob.Push(start);
                    while (blob.Count > 0)
                    {
                        int i = blob.Pop(), x = i % w, y = i / w;
                        n++;
                        if (x < bx0) bx0 = x;
                        if (x > bx1) bx1 = x;
                        if (y < by0) by0 = y;
                        if (y > by1) by1 = y;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx2 = x + (k == 0 ? -1 : k == 1 ? 1 : 0);
                            int ny2 = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                            if (nx2 < x0 || nx2 > x1 || ny2 < y0 || ny2 > y1) continue;
                            int j = ny2 * w + nx2;
                            if (solid[j] || outside[j] || seen[j]) continue;
                            seen[j] = true;
                            blob.Push(j);
                        }
                    }
                    if (n <= bestN) continue;
                    bestN = n; bestX0 = bx0; bestX1 = bx1; bestY0 = by0; bestY1 = by1;
                }
            if (bestN == 0) return null;

            var dia = (bestX1 - bestX0 + 1 + bestY1 - bestY0 + 1) / 2f;
            return new RingGeometry(
                new SFML.System.Vector2f((bestX0 + bestX1) / 2f, (bestY0 + bestY1) / 2f),
                dia, Math.Max(w, h));
        }
    }
}
