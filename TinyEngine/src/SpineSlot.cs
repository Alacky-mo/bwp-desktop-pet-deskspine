using Spine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TinyEngine
{
    /// <summary>
    /// 可放置多个 Spine 骨骼的对象
    /// </summary>
    public class SpineSlot : Renderable, IDisposable
    {
        private readonly Spine.Spine[] slots;
        private Animator animator;

        /// <summary>
        /// 语音播放器。动画控制器整只换掉时要重新塞进去, 所以留一份自己存着
        /// </summary>
        public Speaker? Speaker
        {
            get => speaker;
            set { speaker = value; animator.Speaker = value; }
        }
        private Speaker? speaker;

        public SpineSlot(int slotCount)
        {
            slots = new Spine.Spine[slotCount];
            animator = Animator.New(animatorType, slots);
            animator.Speaker = speaker;
            components["animator"] = animator;
        }

        ~SpineSlot() { Dispose(false); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i]?.Dispose();
                slots[i] = null;
            }
        }

        /// <summary>
        /// 加载 Spine 到指定槽位
        /// </summary>
        public void SetSpine(string? skelPath, int index)
        {
            if (index >= slots.Length)
                throw new ArgumentOutOfRangeException($"Max spine slot count: {slots.Length}, got index {index}");

            if (skelPath == slots[index]?.SkelPath)
                return;

            if (string.IsNullOrEmpty(skelPath))
            {
                slots[index]?.Dispose();
                slots[index] = null;
                Debug.WriteLine($"Unload spine[{index}]");
                // 卸下的是正在出场的那格就换一只, 不然桌宠直接隐形
                if (index == activeSlot) ActiveSlot = index;
                return;
            }

            Debug.WriteLine($"Loading spine[{index}]({version}) from {skelPath}");
            Spine.Spine spineNew = Spine.Spine.New(version, skelPath, defaultMix: 0.15f);

            // 设置位置大小信息
            var originalPosition = Position;
            spineNew.X = originalPosition.X;
            spineNew.Y = originalPosition.Y;
            spineNew.Scale = Scale;
            spineNew.FlipX = Flip;
            spineNew.UsePremultipliedAlpha = UsePremultipliedAlpha;

            slots[index] = spineNew;
            animator.Reset();

            Debug.Write("spine animiation: ");
            foreach (var a in slots[index].AnimationNames) Debug.Write($"{a}; "); Debug.WriteLine("");
        }

        /// <summary>
        /// 获取指定槽位的 Spine 资源路径
        /// </summary>
        public string? GetSpineSkelPath(int index)
        {
            if (index >= slots.Length)
                throw new ArgumentOutOfRangeException($"Max spine slot count: {slots.Length}, got index {index}");
            return slots[index]?.SkelPath;
        }

        /// <summary>
        /// 运行时版本
        /// </summary>
        public SpineVersion Version
        {
            get => version;
            set
            {
                if (version == value) return;
                // 先落版本号: 某个旧层重建失败不该让版本永远切不过去
                version = value;
                var originalPosition = Position;
                var originalScale = Scale;
                var originalFlip = Flip;
                var originalUsePMA = UsePremultipliedAlpha;

                Exception? failure = null;
                for (int i = 0; i < slots.Length; i++)
                {
                    var sp = slots[i];
                    if (sp is null) continue;
                    try
                    {
                        var newSpine = Spine.Spine.New(value, sp.SkelPath, defaultMix: 0.15f);
                        newSpine.X = originalPosition.X;
                        newSpine.Y = originalPosition.Y;
                        newSpine.Scale = originalScale;
                        newSpine.FlipX = originalFlip;
                        newSpine.UsePremultipliedAlpha = originalUsePMA;
                        slots[i] = newSpine;
                        animator.Reset();
                    }
                    catch (Exception ex)
                    {
                        // 该层保持原样, 其余层继续重建, 最后把首个错误报给调用方
                        failure ??= ex;
                    }
                }
                if (failure is not null) throw failure;
            }
        }
        private SpineVersion version = SpineVersion.V38;

        /// <summary>
        /// 左右翻转
        /// </summary>
        public bool Flip
        {
            get => flip;
            set { flip = value; foreach(var sp in slots)  { if (sp is not null) { sp.FlipX = value; } } }
        }
        private bool flip = false;

        /// <summary>
        /// 缩放比例
        /// </summary>
        public float Scale
        {
            get => scale;
            set
            {
                if (Math.Abs(value - scale) < 1e-3) return;
                scale = value;
                foreach (var sp in slots) { if (sp is not null) sp.Scale = value; }
            }
        }
        private float scale = 1f;

        /// <summary>
        /// 是否使用预乘 Alpha
        /// </summary>
        public bool UsePremultipliedAlpha
        {
            get => usePremultipliedAlpha;
            set { usePremultipliedAlpha = value; foreach (var sp in slots) { if (sp is not null) sp.UsePremultipliedAlpha = value; } }
        }
        private bool usePremultipliedAlpha = false;

        /// <summary>
        /// Spine 位置
        /// </summary>
        public SFML.System.Vector2f Position
        {
            get => position;
            set
            {
                position = value;
                foreach (var sp in slots) { if (sp is not null) { sp.X = value.X; sp.Y = value.Y; } }
            }
        }
        private SFML.System.Vector2f position = new(0, 0);

        /// <summary>
        /// 动画交互类型
        /// </summary>
        public AnimatorType AnimatorType
        {
            get => animatorType;
            set
            {
                if (value == animatorType) return;
                animator = Animator.New(value, slots);
                animator.Speaker = speaker;
                animator.ActiveSlot = activeSlot;
                components["animator"] = animator;
                animator.Reset();
                animatorType = value;
            }
        }
        private AnimatorType animatorType = AnimatorType.AzurLaneSD;

        /// <summary>
        /// 出场的槽位。槽位本来是同一个角色的多个图层, 全画就会互相叠在一起;
        /// 把各槽位当成角色名册用时靠这一格只放行一个。-1 = 图层模式, 所有槽位一起显示(原始行为)。
        /// </summary>
        public int ActiveSlot
        {
            get => activeSlot;
            set
            {
                // 指到空槽位上就等于什么都没得看, 退到第一格已装载的
                var target = value >= 0 && (value >= slots.Length || slots[value] is null)
                    ? Array.FindIndex(slots, sp => sp is not null)
                    : value;
                if (activeSlot == target) return;
                activeSlot = target;
                animator.ActiveSlot = activeSlot;   // 先挪门再 Reset: 出场动画只给这一格, 语音也一样
                animator.Reset();
            }
        }
        private int activeSlot = -1;

        /// <summary>
        /// 已装载骨骼的槽位下标
        /// </summary>
        public IEnumerable<int> LoadedSlots => Enumerable.Range(0, slots.Length).Where(i => slots[i] is not null);

        /// <summary>
        /// 出场骨骼的世界包围盒, 给头像框这类要跟着一块的图层当锚点。
        /// 图层模式把所有槽位并起来; 没装载就是零矩形
        /// </summary>
        public SFML.Graphics.FloatRect ActiveWorldBounds
        {
            get
            {
                if (activeSlot >= 0) return slots[activeSlot]?.WorldBounds ?? default;

                var union = default(SFML.Graphics.FloatRect);
                bool any = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    var sp = slots[i];
                    if (sp is null) continue;
                    var b = sp.WorldBounds;
                    if (!any) { union = b; any = true; continue; }
                    var left = Math.Min(union.Left, b.Left);
                    var top = Math.Min(union.Top, b.Top);
                    var right = Math.Max(union.Left + union.Width, b.Left + b.Width);
                    var bottom = Math.Max(union.Top + union.Height, b.Top + b.Height);
                    union = new(left, top, right - left, bottom - top);
                }
                return union;
            }
        }

        /// <summary>
        /// Ctrl+滚轮换一只: 在已装载的槽位里前后挪。图层模式不接手, 滚轮留给动画本身
        /// </summary>
        public override bool SwitchActive(int direction)
        {
            if (activeSlot < 0) return false;

            var loaded = LoadedSlots.ToArray();
            if (loaded.Length < 2) return true;

            var current = Array.IndexOf(loaded, activeSlot);
            var step = direction >= 0 ? 1 : loaded.Length - 1;
            ActiveSlot = loaded[(current + step) % loaded.Length];
            return true;
        }

        /// <summary>
        /// 通知所有槽位整点报时
        /// </summary>
        public void HourlyChime() => animator.HourlyChime();

        /// <summary>
        /// 番茄钟换了阶段, 让动画控制器换张脸
        /// </summary>
        public void FocusPhase(PomodoroPhase from, PomodoroPhase to) => animator.FocusPhase(from, to);

        /// <summary>
        /// 闭嘴开关, 透给动画控制器: 表情照播, 语音不配
        /// </summary>
        public bool Muted
        {
            get => animator.Muted;
            set => animator.Muted = value;
        }

        public override void Render(SFML.Graphics.RenderTarget target)
        {
            base.Render(target);
            if (activeSlot >= 0 && activeSlot < slots.Length)
            {
                var active = slots[activeSlot];
                if (active is not null) target.Draw(active);
                return;
            }
            for (int i = slots.Length - 1; i >= 0; i--)
            {
                var sp = slots[i];
                if (sp is not null) target.Draw(sp);
            }
        }

        public override void Update(float delta)
        {
            base.Update(delta);
            for (int i = 0; i < slots.Length; i++)
            {
                if (activeSlot >= 0 && i != activeSlot) continue;
                slots[i]?.Update(delta);
            }
        }

        public override void Drag(SFML.Window.Mouse.Button button, SFML.System.Vector2f delta, SFML.System.Vector2f deltaFromSrc)
        {
            base.Drag(button, delta, deltaFromSrc);
            if (button == SFML.Window.Mouse.Button.Right)
            {
                Position += delta;
            }
        }
    }
}