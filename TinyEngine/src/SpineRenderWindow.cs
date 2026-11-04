using Microsoft.Win32;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TinyEngine
{
    /// <summary>
    /// 显示窗口
    /// </summary>
    public class SpineRenderWindow : LayeredRenderWindow
    {
        private SpineSlot spineSlots;
        private FrameLayer frames;
        private Speaker speaker = new();
        private Pomodoro pomodoro = new();

        /// <summary>
        /// SpineRenderWindow 基类, 提供 Spine 更新和渲染
        /// </summary>
        public SpineRenderWindow(int slotCount)
        {
            spineSlots = new SpineSlot(slotCount);
            frames = new FrameLayer(slotCount) { Follow = spineSlots };
            // 语音播放器塞给骨骼槽的动画控制器, 切脸时才有机会开口
            spineSlots.Speaker = speaker;
            // 番茄钟只认挂钟, 推进时把阶段丢回骨骼槽; 它自己也要被场景每帧叫醒一次
            pomodoro.Target = spineSlots;
            scene.Renderables["spineSlots"] = spineSlots;
            // 头像框盖在宠物上面, 所以注册在 spineSlots 之后
            scene.Renderables["frames"] = frames;
            scene.Renderables["speaker"] = speaker;
            scene.Renderables["pomodoro"] = pomodoro;
        }

        /// <summary>
        /// 专注期间不许睡: 睡着的脸会把「正在干活」这件事盖掉, 番茄钟等于没做
        /// </summary>
        protected override float SleepLimit()
            => pomodoro.BlockingSleep ? float.PositiveInfinity : base.SleepLimit();

        /// <summary>
        /// 头顶锚点跟着出场的那一格走: 图层模式(ActiveSlot=-1)是所有槽位叠着画, 包围盒就并起来。
        /// 基类的建窗流程在渲染线程上调这里, 那时派生类的字段赋值还没跑, 所以 spineSlots 允许是空的
        /// </summary>
        protected override SFML.Graphics.FloatRect AnchorWorldBounds => spineSlots?.ActiveWorldBounds ?? default;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            scene.Renderables.Clear();
            spineSlots.Dispose();
            frames.Dispose();
            //speaker.Dispose();
        }

        /// <summary>
        /// 操作 Spine 对象属性
        /// </summary>
        public SpineSlot Spine { get => spineSlots; }

        /// <summary>
        /// 操作头像框图层
        /// </summary>
        public FrameLayer Frames { get => frames; }

        /// <summary>
        /// 操作语音属性
        /// </summary>
        public Speaker Speaker { get => speaker; }

        /// <summary>
        /// 番茄钟, 托盘拿它开始/暂停/跳过, 也拿它读还剩多久和今天做了几段
        /// </summary>
        public Pomodoro Pomodoro { get => pomodoro; }

        /// <summary>
        /// 重置窗口和 Spine 的位置和大小
        /// </summary>
        public void ResetPositionAndSize()
        {
            spineSlots.Position = new(0, 0);
            Position = new(0, 0);
            Size = new(1000, 1000);
        }
    }
}

